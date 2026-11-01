-- Nexto (github.com/Rolv-Arild/Necto, by Rolv, Soren and contributors; CC BY-NC-SA 4.0) ported to Luau. Changes: rewritten from PyTorch to Luau, attention folded algebraically (same outputs).
-- Matches the TorchScript model; attention is algebraically folded because there is a single query:
--   score_e = (Q_h/sqrt(d)) . (Wk_h k_e + bk_h)  ->  u_h . k_e  (bk term is constant per head, cancels in softmax)
--   out_h   = sum_e a_e (Wv_h k_e + bv_h)        ->  Wv_h (sum_e a_e k_e) + bv_h
-- so the per-entity K/V projections disappear. Weight rows are packed 3-wide into vectors (~2x faster matvecs).
local Nexto = {}

local vcreate, vzero = vector.create, vector.zero

local function packRows(buf, off, rows, cols)
	local n3 = math.ceil(cols / 3)
	local out = table.create(rows * n3)
	for r = 0, rows - 1 do
		local base = off + r * cols
		for j = 0, n3 - 1 do
			local c = j * 3
			local a = buffer.readf32(buf, (base + c) * 4)
			local b = c + 1 < cols and buffer.readf32(buf, (base + c + 1) * 4) or 0
			local d = c + 2 < cols and buffer.readf32(buf, (base + c + 2) * 4) or 0
			out[r * n3 + j + 1] = vcreate(a, b, d)
		end
	end
	return { w = out, rows = rows, n3 = n3 }
end
local function readVec(buf, off, n)
	local t = table.create(n)
	for i = 1, n do t[i] = buffer.readf32(buf, (off + i - 1) * 4) end
	return t
end

local function pack(x, n)
	local n3 = math.ceil(n / 3)
	local p = table.create(n3)
	for j = 0, n3 - 1 do
		local c = j * 3
		p[j + 1] = vcreate(x[c + 1], x[c + 2] or 0, x[c + 3] or 0)
	end
	return p
end
-- y = W x + b (x packed), optional relu
local function linear(L, xp, bias, relu, out)
	local w, n3 = L.w, L.n3
	out = out or table.create(L.rows)
	for r = 0, L.rows - 1 do
		local acc = vzero
		local base = r * n3
		for j = 1, n3 do acc += w[base + j] * xp[j] end
		local s = acc.x + acc.y + acc.z + (bias and bias[r + 1] or 0)
		if relu and s < 0 then s = 0 end
		out[r + 1] = s
	end
	return out
end
local function layerNorm(x, g, b, n)
	local m = 0
	for i = 1, n do m += x[i] end
	m /= n
	local v = 0
	for i = 1, n do local d = x[i] - m v += d * d end
	local inv = 1 / math.sqrt(v / n + 1e-5)
	local out = table.create(n)
	for i = 1, n do out[i] = (x[i] - m) * inv * g[i] + b[i] end
	return out
end
local function dotp(a, b, n3)
	local acc = vzero
	for j = 1, n3 do acc += a[j] * b[j] end
	return acc.x + acc.y + acc.z
end

function Nexto.load(weightsModule)
	local blob = crypt.base64decode(weightsModule.data)
	local buf = buffer.fromstring(blob)
	local man = weightsModule.manifest
	local function M(name) local e = man[name] return packRows(buf, e[1], e[2], e[3]) end
	local function V(name) local e = man[name] return readVec(buf, e[1], e[2]) end
	local self = { actions = Nexto.ACTIONS }
	local pre = "net.earl."
	self.q0, self.q0b = M(pre .. "query_preprocess.0.weight"), V(pre .. "query_preprocess.0.bias")
	self.q2, self.q2b = M(pre .. "query_preprocess.2.weight"), V(pre .. "query_preprocess.2.bias")
	self.k0, self.k0b = M(pre .. "key_value_preprocess.0.weight"), V(pre .. "key_value_preprocess.0.bias")
	self.k2, self.k2b = M(pre .. "key_value_preprocess.2.weight"), V(pre .. "key_value_preprocess.2.bias")
	self.blocks = {}
	for b = 0, 1 do
		local p = pre .. "blocks." .. b .. "."
		local e = man[p .. "attention.in_proj_weight"]
		local inb = V(p .. "attention.in_proj_bias")
		local blk = {
			Wq = packRows(buf, e[1], 128, 128),
			-- Wk rows kept packed; u_h = sum_i q_i * Wk_row_i (rows of head h)
			Wk = packRows(buf, e[1] + 128 * 128, 128, 128),
			Wv = packRows(buf, e[1] + 256 * 128, 128, 128),
			bq = table.move(inb, 1, 128, 1, {}),
			bv = table.move(inb, 257, 384, 1, {}),
			Wo = M(p .. "attention.out_proj.weight"), bo = V(p .. "attention.out_proj.bias"),
			W1 = M(p .. "linear1.weight"), b1 = V(p .. "linear1.bias"),
			W2 = M(p .. "linear2.weight"), b2 = V(p .. "linear2.bias"),
			n1g = V(p .. "norm1.weight"), n1b = V(p .. "norm1.bias"),
			n2g = V(p .. "norm2.weight"), n2b = V(p .. "norm2.bias"),
			n3g = V(p .. "norm3.weight"), n3b = V(p .. "norm3.bias"),
		}
		self.blocks[b + 1] = blk
	end
	-- action embeddings are input-independent: precompute (90 x 32)
	local o0, o0b = M("net.output.net.0.weight"), V("net.output.net.0.bias")
	local o2, o2b = M("net.output.net.2.weight"), V("net.output.net.2.bias")
	local o4, o4b = M("net.output.net.4.weight"), V("net.output.net.4.bias")
	self.actEmb = {}
	for i, a in Nexto.ACTIONS do
		local h = linear(o0, pack(a, 8), o0b, true)
		h = linear(o2, pack(h, 32), o2b, true)
		h = linear(o4, pack(h, 32), o4b, true)
		self.actEmb[i] = pack(h, 32)
	end
	self.emb, self.embb = M("net.output.emb_convertor.weight"), V("net.output.emb_convertor.bias")
	return setmetatable(self, { __index = Nexto })
end

-- q: 32 numbers, kv: array of 24-number rows. Returns best action index (1-based), logits.
function Nexto:forward(q, kv)
	local x = linear(self.q2, pack(linear(self.q0, pack(q, 32), self.q0b, true), 128), self.q2b, true)
	local keys = table.create(#kv)
	local step = self.step
	for e, row in kv do
		keys[e] = linear(self.k2, pack(linear(self.k0, pack(row, 24), self.k0b, true), 128), self.k2b, true)
		if step then step() end
	end
	local scale = 1 / math.sqrt(32)
	for _, B in self.blocks do
		local qn = layerNorm(x, B.n1g, B.n1b, 128)
		local Q = linear(B.Wq, pack(qn, 128), B.bq)
		local kn = table.create(#keys)
		for e, k in keys do kn[e] = pack(layerNorm(k, B.n2g, B.n2b, 128), 128) end
		if step then step() end
		local attnOut = table.create(128, 0)
		for h = 0, 3 do
			if step then step() end
			-- u_h = Wk_h^T (Q_h * scale)
			local u = table.create(43, vzero)
			local Wk = B.Wk.w
			for i = h * 32, h * 32 + 31 do
				local qi = Q[i + 1] * scale
				local base = i * 43
				for j = 1, 43 do u[j] += Wk[base + j] * qi end
			end
			local scores, mx = table.create(#kn), -math.huge
			for e, k in kn do
				local s = dotp(u, k, 43)
				scores[e] = s
				if s > mx then mx = s end
			end
			local sum = 0
			for e = 1, #scores do local w = math.exp(scores[e] - mx) scores[e] = w sum += w end
			local mix = table.create(43, vzero)
			for e, k in kn do
				local a = scores[e] / sum
				for j = 1, 43 do mix[j] += k[j] * a end
			end
			-- out_h = Wv_h mix + bv_h
			local Wv = B.Wv.w
			for i = h * 32, h * 32 + 31 do
				local acc = vzero
				local base = i * 43
				for j = 1, 43 do acc += Wv[base + j] * mix[j] end
				attnOut[i + 1] = acc.x + acc.y + acc.z + B.bv[i + 1]
			end
		end
		if step then step() end
		local o = linear(B.Wo, pack(attnOut, 128), B.bo)
		for i = 1, 128 do x[i] += o[i] end
		local f = linear(B.W1, pack(layerNorm(x, B.n3g, B.n3b, 128), 128), B.b1, true)
		if step then step() end
		local f2 = linear(B.W2, pack(f, 512), B.b2)
		for i = 1, 128 do x[i] += f2[i] end
	end
	if step then step() end
	for i = 1, 128 do if x[i] < 0 then x[i] = 0 end end
	local emb = pack(linear(self.emb, pack(x, 128), self.embb), 32)
	local best, bestV, logits = 1, -math.huge, table.create(#self.actEmb)
	for i, a in self.actEmb do
		local v = dotp(a, emb, 11)
		logits[i] = v
		if v > bestV then best, bestV = i, v end
	end
	return best, logits
end

---------------------------------------------------------------- observation (nexto_obs.py + rlgym_compat 1.0.2)
Nexto.BOOSTS = { -- rlgym BOOST_LOCATIONS order
	{0,-4240,70},{-1792,-4184,70},{1792,-4184,70},{-3072,-4096,73},{3072,-4096,73},{-940,-3308,70},{940,-3308,70},{0,-2816,70},
	{-3584,-2484,70},{3584,-2484,70},{-1788,-2300,70},{1788,-2300,70},{-2048,-1036,70},{0,-1024,70},{2048,-1036,70},{-3584,0,73},
	{-1024,0,70},{1024,0,70},{3584,0,73},{-2048,1036,70},{0,1024,70},{2048,1036,70},{-1788,2300,70},{1788,2300,70},{-3584,2484,70},
	{3584,2484,70},{0,2816,70},{-940,3310,70},{940,3308,70},{-3072,4096,73},{3072,4096,73},{-1792,4184,70},{1792,4184,70},{0,4240,70},
}
Nexto.KICKOFF = {} -- per-tick scripted kickoff (bot.py KICKOFF_CONTROLS)
do
	local function add(n, a) for _ = 1, n * 4 do table.insert(Nexto.KICKOFF, a) end end
	add(11, {1,0,0,0,0,0,1,0}) add(4, {1,-1,0,0,0,0,1,0}) add(2, {1,0,0,0,0,1,1,0}) add(1, {1,0,0,0,0,0,1,0})
	add(1, {1,0,-0.7,0.8,0,1,1,0}) add(13, {1,0,1,0,0,0,1,0}) add(10, {1,0,0.5,0,1,0,0,0})
end

local INV = {6,7,9,10,12,13,15,16,18,19} -- x,y of pos/vel/fw/up/angvel (1-based)
local function finishRow(r, invert)
	if invert then for _, i in INV do r[i] = -r[i] end end
	for i = 6, 11 do r[i] /= 2300 end
	for i = 18, 20 do r[i] /= 5.5 end
	return r
end
-- arena: Sim arena; me: our car; tick: sim tick; mem: {action, lastGround, padMap} persisted by caller
function Nexto.buildObs(arena, me, tick, mem)
	local invert = me.team == 1
	if not mem.padMap then
		mem.padMap = {}
		for i, L in Nexto.BOOSTS do
			local best, bd
			for j, pad in arena.boostPads do
				local d = vector.magnitude(pad.pos - vector.create(L[1], L[2], L[3]))
				if not bd or d < bd then best, bd = j, d end
			end
			mem.padMap[i] = best
		end
	end
	local kv, q = {}, nil
	for _, car in arena.cars do
		local st = car:GetState()
		if st.isOnGround then mem.lastGround[car.id] = tick end
		local onGround = st.isOnGround or (tick - (mem.lastGround[car.id] or -99)) <= 6
		local p, v, f, u, w = st.pos, st.vel, st.rot.forward, st.rot.up, st.angVel
		local mate = car.team == me.team and 1 or 0
		local row = finishRow({ car == me and 1 or 0, mate, 1 - mate, 0, 0,
			p.x, p.y, p.z, v.x, v.y, v.z, f.x, f.y, f.z, u.x, u.y, u.z, w.x, w.y, w.z,
			st.boost / 100, st.isDemoed and 1 or 0, onGround and 1 or 0,
			(st.hasDoubleJumped or st.hasFlipped) and 0 or 1 }, invert)
		table.insert(kv, row)
		if car == me then q = table.clone(row) end
	end
	local b = arena.ball:GetState()
	table.insert(kv, finishRow({0,0,0,1,0, b.pos.x,b.pos.y,b.pos.z, b.vel.x,b.vel.y,b.vel.z, 0,0,0, 0,0,0, b.angVel.x,b.angVel.y,b.angVel.z, 0,0,0,0}, invert))
	for i, L in Nexto.BOOSTS do
		local pad = arena.boostPads[mem.padMap[i]]
		table.insert(kv, finishRow({0,0,0,0,1, L[1],L[2],L[3], 0,0,0, 0,0,0, 0,0,0, 0,0,0,
			L[3] > 72 and 1 or 0.12, pad and pad.isActive and 1 or 0, 0, 0}, invert))
	end
	for i = 1, 8 do q[24 + i] = mem.action[i] end
	-- relative to self: translate, then rotate x/y by self yaw
	local th = math.atan2(q[12], q[13])
	local ct, sn = math.cos(th), math.sin(th)
	for _, r in kv do
		r[6] -= q[6] r[7] -= q[7] r[8] -= q[8]
		for i = 6, 18, 3 do
			local x, y = r[i], r[i + 1]
			r[i], r[i + 1] = ct * x - sn * y, sn * x + ct * y
		end
	end
	return q, kv
end

-- 90-action table, same generator as the original agent.py
do
	local A = {}
	for _, th in { -1, 0, 1 } do
		for _, st in { -1, 0, 1 } do
			for _, bo in { 0, 1 } do
				for _, hb in { 0, 1 } do
					if not (bo == 1 and th ~= 1) then
						table.insert(A, { (th ~= 0 and th) or bo, st, 0, st, 0, 0, bo, hb })
					end
				end
			end
		end
	end
	for _, p in { -1, 0, 1 } do
		for _, y in { -1, 0, 1 } do
			for _, r in { -1, 0, 1 } do
				for _, j in { 0, 1 } do
					for _, bo in { 0, 1 } do
						if not (j == 1 and y ~= 0) and not (p == 0 and r == 0 and j == 0) then
							local hb = (j == 1 and (p ~= 0 or y ~= 0 or r ~= 0)) and 1 or 0
							table.insert(A, { bo, y, p, y, r, j, bo, hb })
						end
					end
				end
			end
		end
	end
	Nexto.ACTIONS = A
end

return Nexto
