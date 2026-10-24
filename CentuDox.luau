--[[
    CentuDox UI Library v2.0.0
    Glassmorphic, animated, bottom-navigation UI for Roblox executors.

    Load:
        local CentuDox = loadstring(game:HttpGet("YOUR_RAW_URL/CentuDox.luau"))()
]]

local Players = game:GetService("Players")
local TweenService = game:GetService("TweenService")
local UserInputService = game:GetService("UserInputService")
local RunService = game:GetService("RunService")
local CoreGui = game:GetService("CoreGui")

local LocalPlayer = Players.LocalPlayer
local GENV = (type(getgenv) == "function" and getgenv()) or _G

local HEADER_HEIGHT = 56
local NAV_HEIGHT = 64
local BAR_COUNT = 24
local BAR_HEIGHT = 24

local CentuDox = {
    Name = "CentuDox",
    Version = "2.0.0",
    Flags = {},
}

local windows = {}
local windowCount = 0
local spinners = {}
local spinConnection = nil

local THEME = {
    Base = Color3.fromRGB(9, 11, 22),
    Top = Color3.fromRGB(30, 22, 62),
    Surface = Color3.fromRGB(17, 20, 38),
    SurfaceHover = Color3.fromRGB(30, 34, 64),
    Stroke = Color3.fromRGB(80, 90, 155),
    Text = Color3.fromRGB(240, 244, 255),
    Muted = Color3.fromRGB(138, 148, 190),
    Accent = Color3.fromRGB(0, 226, 255),
    Accent2 = Color3.fromRGB(148, 88, 255),
    Accent3 = Color3.fromRGB(255, 72, 178),
    Success = Color3.fromRGB(64, 255, 156),
    Warning = Color3.fromRGB(255, 200, 80),
    Danger = Color3.fromRGB(255, 88, 110),
}

local KIND_COLORS = {
    info = THEME.Accent,
    success = THEME.Success,
    warning = THEME.Warning,
    error = THEME.Danger,
}

local PALETTE = {
    THEME.Accent,
    THEME.Accent2,
    THEME.Accent3,
    THEME.Success,
    THEME.Warning,
    THEME.Danger,
}

-- ============================================================================
-- Utilities
-- ============================================================================

local function make(className, props)
    local instance = Instance.new(className)
    local parent = props and props.Parent
    if props then
        for key, value in pairs(props) do
            if key ~= "Parent" then
                instance[key] = value
            end
        end
    end
    if parent then
        instance.Parent = parent
    end
    return instance
end

local function corner(parent, radius)
    return make("UICorner", {
        CornerRadius = UDim.new(0, radius or 10),
        Parent = parent,
    })
end

local function stroke(parent, color, thickness, transparency)
    return make("UIStroke", {
        ApplyStrokeMode = Enum.ApplyStrokeMode.Border,
        Color = color or THEME.Stroke,
        Thickness = thickness or 1,
        Transparency = transparency or 0,
        Parent = parent,
    })
end

local function padding(parent, horizontal, vertical)
    return make("UIPadding", {
        PaddingLeft = UDim.new(0, horizontal or 0),
        PaddingRight = UDim.new(0, horizontal or 0),
        PaddingTop = UDim.new(0, vertical or 0),
        PaddingBottom = UDim.new(0, vertical or 0),
        Parent = parent,
    })
end

local function tween(object, duration, goal, style, direction)
    local animation = TweenService:Create(
        object,
        TweenInfo.new(duration or 0.2, style or Enum.EasingStyle.Quint, direction or Enum.EasingDirection.Out),
        goal
    )
    animation:Play()
    return animation
end

local function clamp(value, minimum, maximum)
    return math.max(minimum, math.min(maximum, value))
end

local function normalize(options, fallbackLabel)
    if type(options) == "string" then
        return {label = options, title = options}
    end
    options = type(options) == "table" and options or {}
    if fallbackLabel and options.label == nil and options.title == nil then
        options.label = fallbackLabel
    end
    return options
end

local function fire(callback, ...)
    if type(callback) ~= "function" then
        return
    end
    local arguments = table.pack(...)
    task.spawn(function()
        local ok, err = pcall(callback, table.unpack(arguments, 1, arguments.n))
        if not ok then
            warn("[CentuDox] callback error: " .. tostring(err))
        end
    end)
end

local function track(window, connection)
    table.insert(window._conns, connection)
    return connection
end

local function setFlag(flag, value)
    if flag ~= nil then
        CentuDox.Flags[flag] = value
    end
end

local function formatNumber(value, decimals)
    if decimals and decimals > 0 then
        return string.format("%." .. decimals .. "f", value)
    end
    if value == math.floor(value) then
        return tostring(math.floor(value))
    end
    return string.format("%.1f", value)
end

local function keyName(key)
    if typeof(key) == "EnumItem" then
        return key == Enum.KeyCode.Unknown and "None" or key.Name
    end
    return tostring(key or "None")
end

local function resolveGuiParent()
    if type(gethui) == "function" then
        local ok, hidden = pcall(gethui)
        if ok and typeof(hidden) == "Instance" then
            return hidden
        end
    end
    local ok = pcall(function()
        return CoreGui.Name
    end)
    if ok then
        return CoreGui
    end
    return LocalPlayer:WaitForChild("PlayerGui")
end

local function protectGui(gui)
    local protect = rawget(GENV, "protectgui")
    if type(protect) == "function" then
        pcall(protect, gui)
        return
    end
    local synTable = rawget(GENV, "syn")
    if type(synTable) == "table" and type(synTable.protect_gui) == "function" then
        pcall(synTable.protect_gui, gui)
    end
end

local function readPing()
    local ok, value = pcall(function()
        return math.floor(game:GetService("Stats").Network.ServerStatsItem["Data Ping"]:GetValue() + 0.5)
    end)
    return ok and value or nil
end

-- ============================================================================
-- Animated accents (shared rotation loop for every spinning gradient)
-- ============================================================================

local function ensureSpinLoop()
    if spinConnection then
        return
    end
    spinConnection = RunService.RenderStepped:Connect(function(dt)
        for gradient, speed in pairs(spinners) do
            if gradient.Parent then
                gradient.Rotation = (gradient.Rotation + speed * dt) % 360
            else
                spinners[gradient] = nil
            end
        end
    end)
end

local function accentSequence(colors)
    return ColorSequence.new({
        ColorSequenceKeypoint.new(0, colors[1]),
        ColorSequenceKeypoint.new(0.5, colors[2]),
        ColorSequenceKeypoint.new(1, colors[3]),
    })
end

-- Creates an accent-colored gradient that follows the window's accent palette.
-- The parent should be white (or the gradient tints it).
local function accentGradient(window, parent, rotation)
    local gradient = make("UIGradient", {
        Color = accentSequence(window._accent),
        Rotation = rotation or 0,
        Parent = parent,
    })
    table.insert(window._accentGrads, gradient)
    return gradient
end

local function spin(window, gradient, degreesPerSecond)
    spinners[gradient] = degreesPerSecond
    window._spinners[gradient] = true
    ensureSpinLoop()
end

local function makeDraggable(handle, target, window)
    local dragging = false
    local dragStart, startPosition

    track(window, handle.InputBegan:Connect(function(input)
        if input.UserInputType == Enum.UserInputType.MouseButton1 or input.UserInputType == Enum.UserInputType.Touch then
            dragging = true
            dragStart = input.Position
            startPosition = target.Position
            input.Changed:Connect(function()
                if input.UserInputState == Enum.UserInputState.End then
                    dragging = false
                end
            end)
        end
    end))

    track(window, UserInputService.InputChanged:Connect(function(input)
        if dragging and (input.UserInputType == Enum.UserInputType.MouseMovement or input.UserInputType == Enum.UserInputType.Touch) then
            local delta = input.Position - dragStart
            target.Position = UDim2.new(
                startPosition.X.Scale,
                startPosition.X.Offset + delta.X,
                startPosition.Y.Scale,
                startPosition.Y.Offset + delta.Y
            )
        end
    end))
end

-- Icons: accepts an rbxassetid (string or number), a short glyph, or falls
-- back to the first letter of the title inside the circle.
local function makeIcon(parent, icon, fallbackText, color)
    local value = icon
    if type(value) == "number" then
        value = "rbxassetid://" .. value
    end

    if type(value) == "string" and (string.find(value, "rbxassetid", 1, true) or string.find(value, "http", 1, true) == 1) then
        return make("ImageLabel", {
            AnchorPoint = Vector2.new(0.5, 0.5),
            Position = UDim2.fromScale(0.5, 0.5),
            Size = UDim2.fromScale(0.56, 0.56),
            BackgroundTransparency = 1,
            Image = value,
            ImageColor3 = color,
            ScaleType = Enum.ScaleType.Fit,
            Parent = parent,
        })
    end

    local text = (type(value) == "string" and value ~= "") and value or string.upper(string.sub(fallbackText or "?", 1, 1))
    if #text > 3 then
        text = string.upper(string.sub(text, 1, 1))
    end
    return make("TextLabel", {
        Size = UDim2.fromScale(1, 1),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 13,
        TextColor3 = color,
        Text = text,
        Parent = parent,
    })
end

-- ============================================================================
-- Control rows
-- ============================================================================

local function nextOrder(section)
    section._order += 1
    return section._order
end

local function newRow(section, height, searchText)
    local row = make("Frame", {
        Size = UDim2.new(1, 0, 0, height or 34),
        BackgroundColor3 = THEME.SurfaceHover,
        BackgroundTransparency = 0.45,
        BorderSizePixel = 0,
        ClipsDescendants = true,
        LayoutOrder = nextOrder(section),
        Parent = section._body,
    })
    corner(row, 8)
    table.insert(section._rows, {
        frame = row,
        text = string.lower(tostring(searchText or "")),
    })
    return row
end

local function rowLabel(row, text, rightInset)
    return make("TextLabel", {
        Name = "Label",
        Position = UDim2.fromOffset(10, 0),
        Size = UDim2.new(1, -(rightInset or 0) - 20, 1, 0),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 12,
        TextColor3 = THEME.Text,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = text,
        Parent = row,
    })
end

local function addLabel(section, options)
    options = normalize(options, "Label")
    local row = newRow(section, 20, options.text or options.label)
    row.BackgroundTransparency = 1
    row.AutomaticSize = Enum.AutomaticSize.Y
    row.Size = UDim2.new(1, 0, 0, 0)
    local text = make("TextLabel", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 12,
        TextColor3 = options.color or THEME.Muted,
        TextWrapped = true,
        RichText = true,
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = options.text or options.label or "",
        Parent = row,
    })
    return {
        SetText = function(value)
            text.Text = tostring(value)
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addParagraph(section, options)
    options = normalize(options, "Paragraph")
    local window = section._window
    local row = newRow(section, nil, (options.title or "") .. " " .. (options.text or options.content or ""))
    row.AutomaticSize = Enum.AutomaticSize.Y
    row.Size = UDim2.new(1, 0, 0, 0)
    padding(row, 10, 8)
    local layout = make("UIListLayout", {Padding = UDim.new(0, 4), SortOrder = Enum.SortOrder.LayoutOrder, Parent = row})
    local title = make("TextLabel", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 12,
        TextColor3 = Color3.new(1, 1, 1),
        TextXAlignment = Enum.TextXAlignment.Left,
        TextWrapped = true,
        Text = options.title or "",
        LayoutOrder = 1,
        Parent = row,
    })
    accentGradient(window, title, 0)
    local body = make("TextLabel", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Muted,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextWrapped = true,
        RichText = true,
        Text = options.text or options.content or "",
        LayoutOrder = 2,
        Parent = row,
    })
    if options.title == nil or options.title == "" then
        title.Visible = false
    end
    return {
        SetText = function(value)
            body.Text = tostring(value)
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addButton(section, options)
    options = normalize(options, "Button")
    local window = section._window
    local row = newRow(section, 34, options.label)
    row.BackgroundColor3 = Color3.new(1, 1, 1)
    row.BackgroundTransparency = 0.2
    accentGradient(window, row, 0)

    local button = make("TextButton", {
        Size = UDim2.fromScale(1, 1),
        BackgroundTransparency = 1,
        AutoButtonColor = false,
        Font = Enum.Font.GothamBold,
        TextSize = 12,
        TextColor3 = Color3.new(1, 1, 1),
        Text = options.label or "Button",
        Parent = row,
    })

    track(window, button.MouseEnter:Connect(function()
        tween(row, 0.15, {BackgroundTransparency = 0})
    end))
    track(window, button.MouseLeave:Connect(function()
        tween(row, 0.15, {BackgroundTransparency = 0.2})
    end))
    track(window, button.MouseButton1Click:Connect(function()
        tween(row, 0.08, {Size = UDim2.new(1, -6, 0, 30)})
        task.delay(0.08, function()
            if row.Parent then
                tween(row, 0.12, {Size = UDim2.new(1, 0, 0, 34)})
            end
        end)
        fire(options.callback or options.onClick)
    end))

    return {
        SetText = function(value)
            button.Text = tostring(value)
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addToggle(section, options)
    options = normalize(options, "Toggle")
    local window = section._window
    local label = options.label or "Toggle"
    local flag = options.flag or label
    local state = options.default == true
    local row = newRow(section, 34, label)
    rowLabel(row, label, 56)

    local track_ = make("Frame", {
        AnchorPoint = Vector2.new(1, 0.5),
        Position = UDim2.new(1, -10, 0.5, 0),
        Size = UDim2.fromOffset(38, 20),
        BackgroundColor3 = THEME.Base,
        BorderSizePixel = 0,
        Parent = row,
    })
    corner(track_, 10)
    stroke(track_, THEME.Stroke, 1, 0.3)

    local fill = make("Frame", {
        Size = UDim2.fromScale(1, 1),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BackgroundTransparency = 1,
        BorderSizePixel = 0,
        Parent = track_,
    })
    corner(fill, 10)
    accentGradient(window, fill, 0)

    local knob = make("Frame", {
        AnchorPoint = Vector2.new(0, 0.5),
        Position = UDim2.new(0, 3, 0.5, 0),
        Size = UDim2.fromOffset(14, 14),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = track_,
    })
    corner(knob, 7)

    local function render(instant)
        local duration = instant and 0 or 0.22
        tween(fill, duration, {BackgroundTransparency = state and 0 or 1})
        tween(knob, duration, {Position = state and UDim2.new(1, -17, 0.5, 0) or UDim2.new(0, 3, 0.5, 0)})
    end

    local function setState(value, silent)
        state = value == true
        render(false)
        setFlag(flag, state)
        if not silent then
            fire(options.onChange, state)
        end
    end

    local hit = make("TextButton", {
        Size = UDim2.fromScale(1, 1),
        BackgroundTransparency = 1,
        Text = "",
        AutoButtonColor = false,
        Parent = row,
    })
    track(window, hit.MouseButton1Click:Connect(function()
        setState(not state)
    end))

    setFlag(flag, state)
    render(true)

    return {
        Set = function(value, silent)
            setState(value, silent)
        end,
        Get = function()
            return state
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addSlider(section, options)
    options = normalize(options, "Slider")
    local window = section._window
    local name = options.label or "Slider"
    local flag = options.flag or name
    local minimum = options.min or 0
    local maximum = options.max or 100
    local step = options.step or 1
    local suffix = options.suffix or ""
    local decimals = step < 1 and 1 or 0
    local value = clamp(options.default or minimum, minimum, maximum)

    local row = newRow(section, 46, name)

    local label = make("TextLabel", {
        Position = UDim2.fromOffset(10, 6),
        Size = UDim2.new(1, -100, 0, 16),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 12,
        TextColor3 = THEME.Text,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = name,
        Parent = row,
    })

    local valueLabel = make("TextLabel", {
        AnchorPoint = Vector2.new(1, 0),
        Position = UDim2.new(1, -10, 0, 6),
        Size = UDim2.new(0, 90, 0, 16),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 12,
        TextColor3 = Color3.new(1, 1, 1),
        TextXAlignment = Enum.TextXAlignment.Right,
        Parent = row,
    })
    accentGradient(window, valueLabel, 0)

    local track_ = make("Frame", {
        Position = UDim2.new(0, 10, 1, -14),
        Size = UDim2.new(1, -20, 0, 6),
        BackgroundColor3 = THEME.Base,
        BorderSizePixel = 0,
        Parent = row,
    })
    corner(track_, 3)

    local fill = make("Frame", {
        Size = UDim2.fromScale(0, 1),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = track_,
    })
    corner(fill, 3)
    accentGradient(window, fill, 0)

    local knob = make("Frame", {
        AnchorPoint = Vector2.new(0.5, 0.5),
        Position = UDim2.fromScale(0, 0.5),
        Size = UDim2.fromOffset(12, 12),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = track_,
    })
    corner(knob, 6)
    stroke(knob, THEME.Accent, 2, 0)

    local function render()
        local alpha = (value - minimum) / math.max(maximum - minimum, 1e-6)
        fill.Size = UDim2.fromScale(alpha, 1)
        knob.Position = UDim2.fromScale(alpha, 0.5)
        valueLabel.Text = formatNumber(value, decimals) .. suffix
    end

    local function setValue(input, silent)
        local snapped = math.floor((input - minimum) / step + 0.5) * step + minimum
        local newValue = clamp(snapped, minimum, maximum)
        local changed = newValue ~= value
        value = newValue
        render()
        setFlag(flag, value)
        if changed and not silent then
            fire(options.onChange, value)
        end
    end

    local dragging = false
    local function fromX(x)
        local relative = clamp((x - track_.AbsolutePosition.X) / math.max(track_.AbsoluteSize.X, 1), 0, 1)
        setValue(minimum + relative * (maximum - minimum))
    end

    local hit = make("TextButton", {
        Position = UDim2.new(0, 0, 0, 26),
        Size = UDim2.new(1, 0, 0, 20),
        BackgroundTransparency = 1,
        Text = "",
        AutoButtonColor = false,
        Parent = row,
    })
    track(window, hit.InputBegan:Connect(function(input)
        if input.UserInputType == Enum.UserInputType.MouseButton1 or input.UserInputType == Enum.UserInputType.Touch then
            dragging = true
            fromX(input.Position.X)
        end
    end))
    track(window, UserInputService.InputChanged:Connect(function(input)
        if dragging and (input.UserInputType == Enum.UserInputType.MouseMovement or input.UserInputType == Enum.UserInputType.Touch) then
            fromX(input.Position.X)
        end
    end))
    track(window, UserInputService.InputEnded:Connect(function(input)
        if input.UserInputType == Enum.UserInputType.MouseButton1 or input.UserInputType == Enum.UserInputType.Touch then
            dragging = false
        end
    end))

    setValue(value, true)

    return {
        Set = function(newValue, silent)
            setValue(newValue, silent)
        end,
        Get = function()
            return value
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addDropdown(section, options)
    options = normalize(options, "Dropdown")
    local window = section._window
    local name = options.label or "Dropdown"
    local flag = options.flag or name
    local values = options.values or options.options or {}
    local selected = options.default
    if selected == nil then
        selected = values[1]
    end

    local COLLAPSED = 34
    local ITEM_HEIGHT = 24
    local ITEM_GAP = 3

    local row = newRow(section, COLLAPSED, name)
    local open = false

    local head = make("TextButton", {
        Size = UDim2.new(1, 0, 0, COLLAPSED),
        BackgroundTransparency = 1,
        AutoButtonColor = false,
        Text = "",
        Parent = row,
    })
    make("TextLabel", {
        Position = UDim2.fromOffset(10, 0),
        Size = UDim2.new(0.5, 0, 0, COLLAPSED),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 12,
        TextColor3 = THEME.Text,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = name,
        Parent = head,
    })
    local current = make("TextLabel", {
        AnchorPoint = Vector2.new(1, 0),
        Position = UDim2.new(1, -28, 0, 0),
        Size = UDim2.new(0.5, -12, 0, COLLAPSED),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBold,
        TextSize = 11,
        TextColor3 = THEME.Muted,
        TextXAlignment = Enum.TextXAlignment.Right,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = tostring(selected or "None"),
        Parent = head,
    })
    local arrow = make("TextLabel", {
        AnchorPoint = Vector2.new(1, 0),
        Position = UDim2.new(1, -8, 0, 0),
        Size = UDim2.fromOffset(16, COLLAPSED),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBold,
        TextSize = 10,
        TextColor3 = THEME.Accent,
        Text = "▼",
        Parent = head,
    })

    local list = make("Frame", {
        Position = UDim2.fromOffset(6, COLLAPSED),
        Size = UDim2.new(1, -12, 0, 0),
        BackgroundTransparency = 1,
        Visible = false,
        Parent = row,
    })
    make("UIListLayout", {Padding = UDim.new(0, ITEM_GAP), SortOrder = Enum.SortOrder.LayoutOrder, Parent = list})

    local items = {}

    local function listHeight(count)
        return count * (ITEM_HEIGHT + ITEM_GAP)
    end

    local function refreshHighlight()
        for _, item in ipairs(items) do
            local isSelected = item.value == selected
            item.button.TextColor3 = isSelected and THEME.Accent or THEME.Text
            item.button.BackgroundTransparency = isSelected and 0.05 or 0.3
        end
    end

    local function setOpen(state)
        open = state
        if state then
            list.Visible = true
        end
        local targetHeight = state and (COLLAPSED + 4 + listHeight(#items)) or COLLAPSED
        tween(row, 0.22, {Size = UDim2.new(1, 0, 0, targetHeight)})
        arrow.Text = state and "▲" or "▼"
        if not state then
            task.delay(0.22, function()
                if not open then
                    list.Visible = false
                end
            end)
        end
    end

    local function setSelected(value, silent)
        selected = value
        current.Text = tostring(value)
        refreshHighlight()
        setFlag(flag, value)
        if not silent then
            fire(options.onChange, value)
        end
    end

    local function rebuild(newValues)
        for _, item in ipairs(items) do
            item.button:Destroy()
        end
        items = {}
        for index, value in ipairs(newValues) do
            local button = make("TextButton", {
                Size = UDim2.new(1, 0, 0, ITEM_HEIGHT),
                BackgroundColor3 = THEME.Base,
                BackgroundTransparency = 0.3,
                AutoButtonColor = false,
                Font = Enum.Font.GothamMedium,
                TextSize = 11,
                TextColor3 = THEME.Text,
                Text = tostring(value),
                LayoutOrder = index,
                Parent = list,
            })
            corner(button, 6)
            track(window, button.MouseButton1Click:Connect(function()
                setSelected(value)
                setOpen(false)
            end))
            table.insert(items, {button = button, value = value})
        end
        list.Size = UDim2.new(1, -12, 0, listHeight(#items))
        if open then
            row.Size = UDim2.new(1, 0, 0, COLLAPSED + 4 + listHeight(#items))
        end
        refreshHighlight()
    end

    track(window, head.MouseButton1Click:Connect(function()
        setOpen(not open)
    end))

    rebuild(values)
    setSelected(selected, true)

    return {
        Set = function(value, silent)
            setSelected(value, silent)
        end,
        Get = function()
            return selected
        end,
        Refresh = function(newValues)
            values = newValues or {}
            rebuild(values)
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addKeybind(section, options)
    options = normalize(options, "Keybind")
    local window = section._window
    local name = options.label or "Keybind"
    local flag = options.flag or name
    local key = options.default or Enum.KeyCode.Unknown

    local row = newRow(section, 34, name)
    rowLabel(row, name, 110)

    local keyButton = make("TextButton", {
        AnchorPoint = Vector2.new(1, 0.5),
        Position = UDim2.new(1, -8, 0.5, 0),
        Size = UDim2.fromOffset(96, 22),
        BackgroundColor3 = THEME.Base,
        BackgroundTransparency = 0.1,
        AutoButtonColor = false,
        Font = Enum.Font.GothamBold,
        TextSize = 11,
        TextColor3 = THEME.Text,
        Text = keyName(key),
        Parent = row,
    })
    corner(keyButton, 6)
    local keyStroke = stroke(keyButton, THEME.Stroke, 1, 0.2)

    local listening = false
    local connection = nil

    local function setKey(newKey, silent)
        key = newKey
        keyButton.Text = keyName(key)
        setFlag(flag, key)
        if not silent then
            fire(options.onChange, key)
        end
    end

    local function stopListening()
        listening = false
        if connection then
            connection:Disconnect()
            connection = nil
        end
        keyButton.Text = keyName(key)
        tween(keyStroke, 0.2, {Color = THEME.Stroke})
    end

    track(window, keyButton.MouseButton1Click:Connect(function()
        if listening then
            stopListening()
            return
        end
        listening = true
        keyButton.Text = "..."
        tween(keyStroke, 0.2, {Color = THEME.Accent})
        connection = track(window, UserInputService.InputBegan:Connect(function(input)
            if input.UserInputType ~= Enum.UserInputType.Keyboard then
                return
            end
            if input.KeyCode == Enum.KeyCode.Escape then
                stopListening()
                return
            end
            local captured = input.KeyCode
            stopListening()
            setKey(captured)
        end))
    end))

    setFlag(flag, key)

    return {
        Set = function(newKey, silent)
            setKey(newKey, silent)
        end,
        Get = function()
            return key
        end,
        Destroy = function()
            if connection then
                connection:Disconnect()
            end
            row:Destroy()
        end,
    }
end

local function addInput(section, options)
    options = normalize(options, "Input")
    local window = section._window
    local name = options.label or "Input"
    local flag = options.flag or name

    local row = newRow(section, 34, name)
    rowLabel(row, name, 150)

    local box = make("TextBox", {
        AnchorPoint = Vector2.new(1, 0.5),
        Position = UDim2.new(1, -8, 0.5, 0),
        Size = UDim2.new(0.42, 0, 0, 22),
        BackgroundColor3 = THEME.Base,
        BackgroundTransparency = 0.1,
        ClearTextOnFocus = false,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Text,
        PlaceholderText = options.placeholder or "Type here...",
        PlaceholderColor3 = THEME.Muted,
        Text = tostring(options.default or ""),
        Parent = row,
    })
    corner(box, 6)
    stroke(box, THEME.Stroke, 1, 0.4)

    setFlag(flag, box.Text)

    track(window, box.FocusLost:Connect(function(enterPressed)
        setFlag(flag, box.Text)
        if enterPressed or options.submitOnBlur then
            fire(options.onSubmit or options.onChange, box.Text)
        end
    end))

    return {
        Set = function(text)
            box.Text = tostring(text)
            setFlag(flag, box.Text)
        end,
        Get = function()
            return box.Text
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addColor(section, options)
    options = normalize(options, "Color")
    local window = section._window
    local name = options.label or "Color"
    local flag = options.flag or name
    local palette = options.palette or PALETTE
    local current = options.default or palette[1]

    local row = newRow(section, 34, name)
    rowLabel(row, name, #palette * 20 + 10)

    local holder = make("Frame", {
        AnchorPoint = Vector2.new(1, 0.5),
        Position = UDim2.new(1, -8, 0.5, 0),
        Size = UDim2.fromOffset(#palette * 20, 20),
        BackgroundTransparency = 1,
        Parent = row,
    })
    make("UIListLayout", {
        FillDirection = Enum.FillDirection.Horizontal,
        Padding = UDim.new(0, 4),
        SortOrder = Enum.SortOrder.LayoutOrder,
        Parent = holder,
    })

    local swatches = {}
    local function setColor(color, silent)
        current = color
        for _, swatch in ipairs(swatches) do
            swatch.outline.Transparency = (swatch.color == color) and 0 or 1
        end
        setFlag(flag, color)
        if not silent then
            fire(options.onChange, color)
        end
    end

    for index, color in ipairs(palette) do
        local swatch = make("TextButton", {
            Size = UDim2.fromOffset(16, 16),
            BackgroundColor3 = color,
            AutoButtonColor = false,
            Text = "",
            LayoutOrder = index,
            Parent = holder,
        })
        corner(swatch, 5)
        local outline = stroke(swatch, Color3.new(1, 1, 1), 2, 1)
        table.insert(swatches, {button = swatch, outline = outline, color = color})
        track(window, swatch.MouseButton1Click:Connect(function()
            setColor(color)
        end))
    end

    setColor(current, true)

    return {
        Set = function(color, silent)
            setColor(color, silent)
        end,
        Get = function()
            return current
        end,
        Destroy = function()
            row:Destroy()
        end,
    }
end

local function addDivider(section, options)
    options = normalize(options)
    local window = section._window
    local row = newRow(section, 10, "")
    row.BackgroundTransparency = 1
    row.ClipsDescendants = false
    local line = make("Frame", {
        AnchorPoint = Vector2.new(0.5, 0.5),
        Position = UDim2.fromScale(0.5, 0.5),
        Size = UDim2.new(1, -8, 0, 1),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = row,
    })
    accentGradient(window, line, 0)
    return {
        Destroy = function()
            row:Destroy()
        end,
    }
end

local CONTROL_BUILDERS = {
    AddLabel = addLabel,
    AddText = addLabel,
    AddParagraph = addParagraph,
    AddButton = addButton,
    AddToggle = addToggle,
    AddSlider = addSlider,
    AddDropdown = addDropdown,
    AddKeybind = addKeybind,
    AddInput = addInput,
    AddColor = addColor,
    AddDivider = addDivider,
}

-- ============================================================================
-- Sections, pages, tabs
-- ============================================================================

local function createSection(tab, options)
    options = normalize(options, "Section")
    local window = tab._window
    local title = options.title or options.label or "Section"

    local column = options.column
    if column ~= "left" and column ~= "right" then
        column = tab._weights.left <= tab._weights.right and "left" or "right"
    end
    tab._weights[column] += 1
    local parentColumn = column == "right" and tab._right or tab._left

    local card = make("Frame", {
        Name = "Section",
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundColor3 = THEME.Surface,
        BackgroundTransparency = 0.12,
        BorderSizePixel = 0,
        Parent = parentColumn,
    })
    corner(card, 12)
    stroke(card, THEME.Stroke, 1, 0.45)
    padding(card, 10, 10)
    make("UIListLayout", {Padding = UDim.new(0, 7), SortOrder = Enum.SortOrder.LayoutOrder, Parent = card})

    local header = make("Frame", {
        Size = UDim2.new(1, 0, 0, 20),
        BackgroundTransparency = 1,
        LayoutOrder = 0,
        Parent = card,
    })
    local titleLabel = make("TextLabel", {
        Size = UDim2.new(1, -110, 1, 0),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 12,
        TextColor3 = Color3.new(1, 1, 1),
        TextXAlignment = Enum.TextXAlignment.Left,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = string.upper(title),
        Parent = header,
    })
    accentGradient(window, titleLabel, 0)

    if options.category then
        local pill = make("TextLabel", {
            AnchorPoint = Vector2.new(1, 0.5),
            Position = UDim2.new(1, 0, 0.5, 0),
            AutomaticSize = Enum.AutomaticSize.X,
            Size = UDim2.new(0, 0, 0, 16),
            BackgroundColor3 = THEME.SurfaceHover,
            BackgroundTransparency = 0.2,
            Font = Enum.Font.GothamMedium,
            TextSize = 9,
            TextColor3 = THEME.Muted,
            Text = string.upper(tostring(options.category)),
            Parent = header,
        })
        corner(pill, 6)
        padding(pill, 6, 0)
    end

    local body = make("Frame", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        LayoutOrder = 1,
        Parent = card,
    })
    make("UIListLayout", {Padding = UDim.new(0, 6), SortOrder = Enum.SortOrder.LayoutOrder, Parent = body})

    local section = {
        _tab = tab,
        _window = window,
        _body = body,
        _rows = {},
        _order = 0,
        _column = column,
        card = card,
    }

    for name, builder in pairs(CONTROL_BUILDERS) do
        section[name] = function(controlOptions)
            return builder(section, controlOptions)
        end
    end

    function section.Destroy()
        card:Destroy()
        for index, existing in ipairs(tab._sections) do
            if existing == section then
                table.remove(tab._sections, index)
                break
            end
        end
    end

    table.insert(tab._sections, section)
    return section
end

local function buildPage(window)
    local page = make("CanvasGroup", {
        Name = "Page",
        Size = UDim2.fromScale(1, 1),
        BackgroundTransparency = 1,
        BorderSizePixel = 0,
        GroupTransparency = 1,
        Visible = false,
        Parent = window._contentArea,
    })

    local scroll = make("ScrollingFrame", {
        Size = UDim2.new(1, -12, 1, -10),
        Position = UDim2.fromOffset(6, 5),
        BackgroundTransparency = 1,
        BorderSizePixel = 0,
        ScrollBarThickness = 3,
        ScrollBarImageColor3 = window._accent[1],
        ScrollingDirection = Enum.ScrollingDirection.Y,
        CanvasSize = UDim2.fromOffset(0, 0),
        Parent = page,
    })

    local left = make("Frame", {
        Size = UDim2.new(0.5, -5, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Parent = scroll,
    })
    local leftLayout = make("UIListLayout", {Padding = UDim.new(0, 8), SortOrder = Enum.SortOrder.LayoutOrder, Parent = left})

    local right = make("Frame", {
        Position = UDim2.new(0.5, 5, 0, 0),
        Size = UDim2.new(0.5, -5, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Parent = scroll,
    })
    local rightLayout = make("UIListLayout", {Padding = UDim.new(0, 8), SortOrder = Enum.SortOrder.LayoutOrder, Parent = right})

    local function refreshCanvas()
        local height = math.max(leftLayout.AbsoluteContentSize.Y, rightLayout.AbsoluteContentSize.Y)
        scroll.CanvasSize = UDim2.fromOffset(0, height + 16)
    end
    track(window, leftLayout:GetPropertyChangedSignal("AbsoluteContentSize"):Connect(refreshCanvas))
    track(window, rightLayout:GetPropertyChangedSignal("AbsoluteContentSize"):Connect(refreshCanvas))
    task.defer(refreshCanvas)

    return page, scroll, left, right
end

local function setTabActive(tab, active)
    tab._active = active
    local page = tab._page
    tween(tab._glow, 0.25, {BackgroundTransparency = active and 0.72 or 1})
    tween(tab._iconHolder, 0.25, {BackgroundColor3 = active and THEME.Surface or THEME.SurfaceHover})
    tween(tab._label, 0.25, {TextColor3 = active and THEME.Text or THEME.Muted})
    tween(tab._indicator, 0.3, {Size = active and UDim2.fromOffset(34, 3) or UDim2.fromOffset(0, 3)})

    if active then
        page.Visible = true
        page.Position = UDim2.fromOffset(16, 0)
        tween(page, 0.35, {GroupTransparency = 0, Position = UDim2.fromOffset(0, 0)})
    else
        tween(page, 0.2, {GroupTransparency = 1, Position = UDim2.fromOffset(-16, 0)})
        task.delay(0.22, function()
            if not tab._active then
                page.Visible = false
            end
        end)
    end
end

local function selectTab(window, tab)
    if window._destroyed or window._activeTab == tab then
        return
    end
    local previous = window._activeTab
    window._activeTab = tab
    if previous then
        setTabActive(previous, false)
    end
    setTabActive(tab, true)
end

local function defaultSection(tab)
    if not tab._defaultSection then
        tab._defaultSection = createSection(tab, {title = tab.title, column = "left"})
    end
    return tab._defaultSection
end

local function createTab(window, options)
    options = normalize(options, "Tab")
    local title = options.title or options.label or options.name or "Tab"
    local id = options.id or title
    local order = #window._tabList + 1

    local page, scroll, left, right = buildPage(window)
    page.Name = "Page_" .. tostring(id)

    local button = make("TextButton", {
        Name = "Tab_" .. tostring(id),
        Size = UDim2.fromOffset(74, 52),
        BackgroundTransparency = 1,
        AutoButtonColor = false,
        Text = "",
        LayoutOrder = order,
        Parent = window._navStrip,
    })

    local glow = make("Frame", {
        Size = UDim2.fromScale(1, 1),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BackgroundTransparency = 1,
        BorderSizePixel = 0,
        Parent = button,
    })
    corner(glow, 12)
    accentGradient(window, glow, 90)

    local iconHolder = make("Frame", {
        AnchorPoint = Vector2.new(0.5, 0),
        Position = UDim2.new(0.5, 0, 0, 4),
        Size = UDim2.fromOffset(26, 26),
        BackgroundColor3 = THEME.SurfaceHover,
        BackgroundTransparency = 0.1,
        BorderSizePixel = 0,
        Parent = button,
    })
    corner(iconHolder, 13)
    stroke(iconHolder, THEME.Stroke, 1, 0.2)
    makeIcon(iconHolder, options.icon, title, THEME.Text)

    local label = make("TextLabel", {
        Position = UDim2.new(0, 0, 0, 34),
        Size = UDim2.new(1, 0, 0, 14),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 10,
        TextColor3 = THEME.Muted,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = title,
        Parent = button,
    })

    local indicator = make("Frame", {
        AnchorPoint = Vector2.new(0.5, 1),
        Position = UDim2.new(0.5, 0, 1, 0),
        Size = UDim2.fromOffset(0, 3),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = button,
    })
    corner(indicator, 2)
    accentGradient(window, indicator, 0)

    local tab = {
        _window = window,
        _page = page,
        _scroll = scroll,
        _left = left,
        _right = right,
        _button = button,
        _glow = glow,
        _iconHolder = iconHolder,
        _label = label,
        _indicator = indicator,
        _sections = {},
        _weights = {left = 0, right = 0},
        _active = false,
        id = id,
        title = title,
    }

    window._tabs[id] = tab
    table.insert(window._tabList, tab)

    track(window, button.MouseButton1Click:Connect(function()
        selectTab(window, tab)
    end))

    function tab.Select()
        selectTab(window, tab)
    end

    function tab.CreateSection(sectionOptions)
        return createSection(tab, sectionOptions)
    end

    for name in pairs(CONTROL_BUILDERS) do
        tab[name] = function(controlOptions)
            return defaultSection(tab)[name](controlOptions)
        end
    end

    if not window._activeTab then
        selectTab(window, tab)
    end

    return tab
end

-- ============================================================================
-- Dashboard
-- ============================================================================

local function createCard(parent, height)
    local card = make("Frame", {
        Size = UDim2.new(1, 0, 0, height),
        BackgroundColor3 = THEME.Surface,
        BackgroundTransparency = 0.12,
        BorderSizePixel = 0,
        Parent = parent,
    })
    corner(card, 12)
    stroke(card, THEME.Stroke, 1, 0.45)
    return card
end

local function pickColumn(tab, column)
    if column == "left" or column == "right" then
        return column
    end
    return tab._weights.left <= tab._weights.right and "left" or "right"
end

local function renderStat(stat)
    local history = stat.history
    local peak = stat.max or 1
    if not stat.max then
        for _, sample in ipairs(history) do
            if sample > peak then
                peak = sample
            end
        end
    end
    local missing = BAR_COUNT - #history
    for index, bar in ipairs(stat.bars) do
        local sample = history[index - missing]
        local fraction = sample and clamp(sample / peak, 0.08, 1) or 0.08
        tween(bar, 0.35, {Size = UDim2.new(1 / BAR_COUNT, -2, 0, math.max(2, fraction * BAR_HEIGHT))})
    end
end

local function addStatCard(window, tab, options)
    options = normalize(options, "Stat")
    local column = pickColumn(tab, options.column)
    local parent = column == "right" and tab._right or tab._left
    tab._weights[column] += 1

    local card = createCard(parent, 100)
    padding(card, 12, 10)

    local label = make("TextLabel", {
        Size = UDim2.new(1, -40, 0, 14),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Muted,
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = string.upper(options.label or options.key or "STAT"),
        Parent = card,
    })
    local iconBox = make("Frame", {
        AnchorPoint = Vector2.new(1, 0),
        Position = UDim2.new(1, 0, 0, 0),
        Size = UDim2.fromOffset(22, 22),
        BackgroundTransparency = 1,
        Parent = card,
    })
    makeIcon(iconBox, options.icon, options.label or "S", THEME.Accent)

    local valueLabel = make("TextLabel", {
        Position = UDim2.fromOffset(0, 16),
        Size = UDim2.new(1, 0, 0, 32),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 26,
        TextColor3 = Color3.new(1, 1, 1),
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = "--",
        Parent = card,
    })
    accentGradient(window, valueLabel, 0)

    local barsHolder = make("Frame", {
        AnchorPoint = Vector2.new(0, 1),
        Position = UDim2.new(0, 0, 1, 0),
        Size = UDim2.new(1, 0, 0, BAR_HEIGHT),
        BackgroundTransparency = 1,
        Parent = card,
    })

    local bars = {}
    for index = 1, BAR_COUNT do
        local bar = make("Frame", {
            AnchorPoint = Vector2.new(0, 1),
            Position = UDim2.new((index - 1) / BAR_COUNT, 1, 1, 0),
            Size = UDim2.new(1 / BAR_COUNT, -2, 0, 2),
            BackgroundColor3 = Color3.new(1, 1, 1),
            BackgroundTransparency = 0.25,
            BorderSizePixel = 0,
            Parent = barsHolder,
        })
        corner(bar, 2)
        accentGradient(window, bar, 90)
        table.insert(bars, bar)
    end

    local stat = {
        key = options.key or options.label,
        history = {},
        bars = bars,
        valueLabel = valueLabel,
        suffix = options.suffix or "",
        max = options.max,
        card = card,
    }
    window._stats[stat.key] = stat
    return stat
end

local function setStat(window, key, value)
    local stat = window._stats[key]
    if not stat or window._destroyed then
        return
    end
    stat.valueLabel.Text = (type(value) == "number" and formatNumber(value) or tostring(value)) .. stat.suffix
    if type(value) == "number" then
        table.insert(stat.history, value)
        if #stat.history > BAR_COUNT then
            table.remove(stat.history, 1)
        end
        renderStat(stat)
    end
end

local function buildDashboard(window, options)
    local tab = createTab(window, {
        id = "dashboard",
        title = options.title or "Dashboard",
        icon = options.icon or "D",
    })

    -- Profile card
    local profile = createCard(tab._left, 92)
    local avatar = make("ImageLabel", {
        Position = UDim2.fromOffset(12, 12),
        Size = UDim2.fromOffset(66, 66),
        BackgroundColor3 = THEME.Base,
        Image = "",
        Parent = profile,
    })
    corner(avatar, 14)
    stroke(avatar, THEME.Accent, 1.5, 0.2)

    task.spawn(function()
        local ok, content = pcall(function()
            return Players:GetUserThumbnailAsync(LocalPlayer.UserId, Enum.ThumbnailType.HeadShot, Enum.ThumbnailSize.Size100x100)
        end)
        if ok and content and avatar.Parent then
            avatar.Image = content
        end
    end)

    local displayName = make("TextLabel", {
        Position = UDim2.fromOffset(92, 14),
        Size = UDim2.new(1, -104, 0, 22),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 16,
        TextColor3 = THEME.Text,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextTruncate = Enum.TextTruncate.AtEnd,
        Text = options.displayName or LocalPlayer.DisplayName,
        Parent = profile,
    })
    local handle = make("TextLabel", {
        Position = UDim2.fromOffset(92, 36),
        Size = UDim2.new(1, -104, 0, 14),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Muted,
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = "@" .. (options.handle or LocalPlayer.Name),
        Parent = profile,
    })
    local planPill = make("TextLabel", {
        Position = UDim2.fromOffset(92, 58),
        Size = UDim2.fromOffset(0, 18),
        AutomaticSize = Enum.AutomaticSize.X,
        BackgroundColor3 = Color3.new(1, 1, 1),
        Font = Enum.Font.GothamBlack,
        TextSize = 9,
        TextColor3 = THEME.Base,
        Text = string.upper(options.plan or "Member"),
        Parent = profile,
    })
    corner(planPill, 6)
    padding(planPill, 8, 0)
    accentGradient(window, planPill, 0)

    -- Subscription card
    local sub = createCard(tab._right, 86)
    padding(sub, 12, 10)
    make("TextLabel", {
        Size = UDim2.new(1, 0, 0, 14),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Muted,
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = "SUBSCRIPTION",
        Parent = sub,
    })
    local durationLabel = make("TextLabel", {
        Position = UDim2.fromOffset(0, 18),
        Size = UDim2.new(1, 0, 0, 24),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 18,
        TextColor3 = Color3.new(1, 1, 1),
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = options.duration or "Active",
        Parent = sub,
    })
    accentGradient(window, durationLabel, 0)
    local subTrack = make("Frame", {
        AnchorPoint = Vector2.new(0, 1),
        Position = UDim2.new(0, 0, 1, 0),
        Size = UDim2.new(1, 0, 0, 6),
        BackgroundColor3 = THEME.Base,
        BorderSizePixel = 0,
        Parent = sub,
    })
    corner(subTrack, 3)
    local subFill = make("Frame", {
        Size = UDim2.fromScale(clamp(options.progress or 1, 0, 1), 1),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = subTrack,
    })
    corner(subFill, 3)
    accentGradient(window, subFill, 0)

    window._subscription = {label = durationLabel, fill = subFill}

    -- Default live stats
    addStatCard(window, tab, {key = "fps", label = "FPS", column = "left", icon = options.fpsIcon})
    addStatCard(window, tab, {key = "ping", label = "Ping", suffix = " ms", column = "right", icon = options.pingIcon})

    local frameCount = 0
    local lastTime = os.clock()
    track(window, RunService.Heartbeat:Connect(function()
        frameCount += 1
        local now = os.clock()
        if now - lastTime >= 0.5 then
            local fps = frameCount / (now - lastTime)
            frameCount = 0
            lastTime = now
            setStat(window, "fps", math.floor(fps + 0.5))
        end
    end))

    task.spawn(function()
        while not window._destroyed do
            local ping = readPing()
            if ping then
                setStat(window, "ping", ping)
            end
            task.wait(1)
        end
    end)

    return tab
end

-- ============================================================================
-- Notifications
-- ============================================================================

local function notify(window, options)
    if window._destroyed then
        return nil
    end
    options = normalize(options, "Notification")
    local kind = options.kind or options.type or "info"
    local color = KIND_COLORS[kind] or THEME.Accent
    local duration = options.duration or 4

    window._toastCounter += 1
    local toast = make("Frame", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundColor3 = THEME.Surface,
        BackgroundTransparency = 0.05,
        BorderSizePixel = 0,
        LayoutOrder = window._toastCounter,
        Parent = window._toastHolder,
    })
    corner(toast, 12)
    local outline = stroke(toast, color, 1.5, 0.2)
    padding(toast, 12, 9)
    make("UIListLayout", {Padding = UDim.new(0, 3), SortOrder = Enum.SortOrder.LayoutOrder, Parent = toast})

    local title = make("TextLabel", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 13,
        TextColor3 = color,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextWrapped = true,
        Text = options.title or "Notice",
        LayoutOrder = 1,
        Parent = toast,
    })
    local body = make("TextLabel", {
        Size = UDim2.new(1, 0, 0, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Text,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextWrapped = true,
        Text = options.text or options.content or "",
        LayoutOrder = 2,
        Parent = toast,
    })
    local barTrack = make("Frame", {
        Size = UDim2.new(1, 0, 0, 3),
        BackgroundColor3 = THEME.Base,
        BorderSizePixel = 0,
        LayoutOrder = 3,
        Parent = toast,
    })
    corner(barTrack, 2)
    local bar = make("Frame", {
        Size = UDim2.fromScale(1, 1),
        BackgroundColor3 = color,
        BorderSizePixel = 0,
        Parent = barTrack,
    })
    corner(bar, 2)
    tween(bar, duration, {Size = UDim2.fromScale(0, 1)}, Enum.EasingStyle.Linear)

    local dismissed = false
    local function dismiss()
        if dismissed or not toast.Parent then
            return
        end
        dismissed = true
        tween(toast, 0.25, {BackgroundTransparency = 1})
        tween(outline, 0.25, {Transparency = 1})
        tween(title, 0.25, {TextTransparency = 1})
        tween(body, 0.25, {TextTransparency = 1})
        tween(barTrack, 0.25, {BackgroundTransparency = 1})
        tween(bar, 0.25, {BackgroundTransparency = 1})
        task.delay(0.3, function()
            if toast.Parent then
                toast:Destroy()
            end
        end)
    end

    task.delay(duration, dismiss)
    return {Dismiss = dismiss}
end

-- ============================================================================
-- Window
-- ============================================================================

local function applySearch(window)
    local query = window._query
    for _, tab in ipairs(window._tabList) do
        for _, section in ipairs(tab._sections) do
            local anyVisible = false
            for _, record in ipairs(section._rows) do
                local match = query == "" or string.find(record.text, query, 1, true) ~= nil
                record.frame.Visible = match
                if match then
                    anyVisible = true
                end
            end
            section.card.Visible = query == "" or anyVisible
        end
    end
end

local function setMinimized(window, state)
    if window._minimized == state or window._destroyed then
        return
    end
    window._minimized = state
    window._contentArea.Visible = not state
    window._nav.Visible = not state
    tween(window._root, 0.35, {
        Size = state and UDim2.fromOffset(window._width, HEADER_HEIGHT) or UDim2.fromOffset(window._width, window._height),
    })
    window._minimizeButton.Text = state and "+" or "-"
end

local function fitScale(window)
    local camera = workspace.CurrentCamera
    local viewport = camera and camera.ViewportSize or Vector2.new(1280, 720)
    local scale = math.min(1, (viewport.X - 32) / window._width, (viewport.Y - 48) / window._height)
    window._uiScale.Scale = math.max(0.45, scale)
end

local function unloadWindow(window)
    if window._destroyed then
        return
    end
    window._destroyed = true

    for _, connection in ipairs(window._conns) do
        pcall(function()
            connection:Disconnect()
        end)
    end
    window._conns = {}

    for gradient in pairs(window._spinners) do
        spinners[gradient] = nil
    end
    window._spinners = {}

    if window._screen then
        window._screen:Destroy()
    end

    for index = #windows, 1, -1 do
        if windows[index] == window then
            table.remove(windows, index)
        end
    end

    if #windows == 0 and spinConnection then
        spinConnection:Disconnect()
        spinConnection = nil
    end

    fire(window._onUnload)
end

function CentuDox.CreateWindow(options)
    options = type(options) == "table" and options or {}
    windowCount += 1

    local width = options.width or 640
    local height = options.height or 430
    local accent = {
        options.accent or THEME.Accent,
        options.accent2 or THEME.Accent2,
        options.accent3 or THEME.Accent3,
    }

    local window = {
        _id = windowCount,
        _conns = {},
        _spinners = {},
        _accentGrads = {},
        _accent = accent,
        _tabs = {},
        _tabList = {},
        _activeTab = nil,
        _query = "",
        _destroyed = false,
        _minimized = false,
        _width = width,
        _height = height,
        _toggleKey = options.toggleKey or Enum.KeyCode.RightShift,
        _onUnload = options.onUnload,
        _stats = {},
        _toastCounter = 0,
        _subscription = nil,
    }
    table.insert(windows, window)

    local screen = make("ScreenGui", {
        Name = "CentuDox_" .. window._id,
        ResetOnSpawn = false,
        ZIndexBehavior = Enum.ZIndexBehavior.Sibling,
        DisplayOrder = options.displayOrder or 999,
    })
    protectGui(screen)
    screen.Parent = resolveGuiParent()
    window._screen = screen

    local root = make("Frame", {
        Name = "Root",
        AnchorPoint = Vector2.new(0.5, 0.5),
        Position = UDim2.fromScale(0.5, 0.5),
        Size = UDim2.fromOffset(width, height),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BackgroundTransparency = 0.04,
        BorderSizePixel = 0,
        ClipsDescendants = true,
        Parent = screen,
    })
    corner(root, 18)
    window._root = root

    local uiScale = make("UIScale", {Scale = 1, Parent = root})
    window._uiScale = uiScale
    fitScale(window)
    track(window, workspace:GetPropertyChangedSignal("CurrentCamera"):Connect(function()
        fitScale(window)
    end))

    make("UIGradient", {
        Color = ColorSequence.new(THEME.Top, THEME.Base),
        Rotation = 90,
        Parent = root,
    })

    local rootStroke = stroke(root, Color3.new(1, 1, 1), 2, 0)
    spin(window, accentGradient(window, rootStroke, 0), 35)

    -- Ambient glows
    local glowA = make("Frame", {
        Size = UDim2.fromOffset(320, 320),
        Position = UDim2.new(1, -150, 0, -170),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BackgroundTransparency = 0.8,
        BorderSizePixel = 0,
        ZIndex = 0,
        Parent = root,
    })
    corner(glowA, 160)
    accentGradient(window, glowA, 45)
    local glowB = make("Frame", {
        Size = UDim2.fromOffset(280, 280),
        Position = UDim2.new(0, -140, 1, -120),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BackgroundTransparency = 0.86,
        BorderSizePixel = 0,
        ZIndex = 0,
        Parent = root,
    })
    corner(glowB, 140)
    accentGradient(window, glowB, 135)

    -- Header
    local header = make("Frame", {
        Name = "Header",
        Size = UDim2.new(1, 0, 0, HEADER_HEIGHT),
        BackgroundTransparency = 1,
        Parent = root,
    })
    makeDraggable(header, root, window)

    local title = make("TextLabel", {
        Position = UDim2.fromOffset(18, 9),
        Size = UDim2.new(0, 280, 0, 24),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamBlack,
        TextSize = 22,
        TextColor3 = Color3.new(1, 1, 1),
        TextXAlignment = Enum.TextXAlignment.Left,
        Text = options.title or "CentuDox",
        Parent = header,
    })
    accentGradient(window, title, 0)

    make("TextLabel", {
        Position = UDim2.fromOffset(18, 33),
        Size = UDim2.new(0, 360, 0, 14),
        BackgroundTransparency = 1,
        Font = Enum.Font.GothamMedium,
        TextSize = 11,
        TextColor3 = THEME.Muted,
        TextXAlignment = Enum.TextXAlignment.Left,
        TextTruncate = Enum.TextTruncate.AtEnd,
        RichText = true,
        Text = options.subtitle or "Premium Edition",
        Parent = header,
    })

    local searchFrame = make("Frame", {
        AnchorPoint = Vector2.new(1, 0.5),
        Position = UDim2.new(1, -84, 0.5, 0),
        Size = UDim2.fromOffset(176, 28),
        BackgroundColor3 = THEME.Surface,
        BackgroundTransparency = 0.2,
        BorderSizePixel = 0,
        Parent = header,
    })
    corner(searchFrame, 9)
    stroke(searchFrame, THEME.Stroke, 1, 0.3)
    local searchBox = make("TextBox", {
        Size = UDim2.new(1, -20, 1, 0),
        Position = UDim2.fromOffset(10, 0),
        BackgroundTransparency = 1,
        ClearTextOnFocus = false,
        PlaceholderText = "Search controls...",
        PlaceholderColor3 = THEME.Muted,
        Text = "",
        TextColor3 = THEME.Text,
        Font = Enum.Font.GothamMedium,
        TextSize = 12,
        TextXAlignment = Enum.TextXAlignment.Left,
        Parent = searchFrame,
    })
    track(window, searchBox:GetPropertyChangedSignal("Text"):Connect(function()
        window._query = string.lower(searchBox.Text)
        applySearch(window)
    end))

    local function headerButton(text, offsetX, callback)
        local button = make("TextButton", {
            AnchorPoint = Vector2.new(1, 0.5),
            Position = UDim2.new(1, offsetX, 0.5, 0),
            Size = UDim2.fromOffset(28, 28),
            BackgroundColor3 = THEME.Surface,
            BackgroundTransparency = 0.2,
            BorderSizePixel = 0,
            AutoButtonColor = false,
            Font = Enum.Font.GothamBold,
            TextSize = 16,
            TextColor3 = THEME.Text,
            Text = text,
            Parent = header,
        })
        corner(button, 9)
        stroke(button, THEME.Stroke, 1, 0.3)
        track(window, button.MouseEnter:Connect(function()
            tween(button, 0.15, {BackgroundColor3 = THEME.SurfaceHover})
        end))
        track(window, button.MouseLeave:Connect(function()
            tween(button, 0.15, {BackgroundColor3 = THEME.Surface})
        end))
        track(window, button.MouseButton1Click:Connect(callback))
        return button
    end

    window._minimizeButton = headerButton("-", -46, function()
        setMinimized(window, not window._minimized)
    end)
    headerButton("×", -12, function()
        unloadWindow(window)
    end)

    -- Content
    local contentArea = make("Frame", {
        Name = "Content",
        Position = UDim2.fromOffset(0, HEADER_HEIGHT),
        Size = UDim2.new(1, 0, 1, -(HEADER_HEIGHT + NAV_HEIGHT)),
        BackgroundTransparency = 1,
        ClipsDescendants = true,
        Parent = root,
    })
    window._contentArea = contentArea

    -- Bottom navigation
    local nav = make("Frame", {
        Name = "Nav",
        Position = UDim2.new(0, 0, 1, -NAV_HEIGHT),
        Size = UDim2.new(1, 0, 0, NAV_HEIGHT),
        BackgroundColor3 = THEME.Surface,
        BackgroundTransparency = 0.15,
        BorderSizePixel = 0,
        Parent = root,
    })
    window._nav = nav

    local navLine = make("Frame", {
        Size = UDim2.new(1, 0, 0, 1),
        BackgroundColor3 = Color3.new(1, 1, 1),
        BorderSizePixel = 0,
        Parent = nav,
    })
    accentGradient(window, navLine, 0)

    window._navStrip = make("ScrollingFrame", {
        Size = UDim2.new(1, -16, 1, -8),
        Position = UDim2.fromOffset(8, 4),
        BackgroundTransparency = 1,
        BorderSizePixel = 0,
        ScrollBarThickness = 0,
        ScrollingDirection = Enum.ScrollingDirection.X,
        CanvasSize = UDim2.fromOffset(0, 0),
        AutomaticCanvasSize = Enum.AutomaticSize.X,
        Parent = nav,
    })
    make("UIListLayout", {
        FillDirection = Enum.FillDirection.Horizontal,
        HorizontalAlignment = Enum.HorizontalAlignment.Center,
        VerticalAlignment = Enum.VerticalAlignment.Center,
        Padding = UDim.new(0, 6),
        SortOrder = Enum.SortOrder.LayoutOrder,
        Parent = window._navStrip,
    })

    -- Toast notifications
    window._toastHolder = make("Frame", {
        Name = "Toasts",
        AnchorPoint = Vector2.new(1, 0),
        Position = UDim2.new(1, -16, 0, 16),
        Size = UDim2.fromOffset(270, 0),
        AutomaticSize = Enum.AutomaticSize.Y,
        BackgroundTransparency = 1,
        Parent = screen,
    })
    make("UIListLayout", {
        Padding = UDim.new(0, 8),
        SortOrder = Enum.SortOrder.LayoutOrder,
        HorizontalAlignment = Enum.HorizontalAlignment.Right,
        Parent = window._toastHolder,
    })

    -- Toggle key
    track(window, UserInputService.InputBegan:Connect(function(input, processed)
        if processed then
            return
        end
        if input.KeyCode == window._toggleKey then
            screen.Enabled = not screen.Enabled
        end
    end))

    -- Public API
    local api = {}
    window._api = api

    function api.CreateTab(tabOptions)
        return createTab(window, tabOptions)
    end
    api.Tab = api.CreateTab

    function api.SelectTab(id)
        local tab = window._tabs[id]
        if tab then
            selectTab(window, tab)
        end
    end

    function api.SetStat(key, value)
        setStat(window, key, value)
    end

    function api.AddStat(statOptions)
        local dashboard = window._tabs.dashboard
        if not dashboard then
            return nil
        end
        return addStatCard(window, dashboard, statOptions)
    end

    function api.SetSubscription(text, progress)
        local sub = window._subscription
        if not sub then
            return
        end
        if text ~= nil then
            sub.label.Text = tostring(text)
        end
        if progress ~= nil then
            tween(sub.fill, 0.4, {Size = UDim2.fromScale(clamp(progress, 0, 1), 1)})
        end
    end

    function api.Notify(notification)
        return notify(window, notification)
    end

    function api.SetAccent(color1, color2, color3)
        window._accent = {
            color1 or window._accent[1],
            color2 or window._accent[2],
            color3 or window._accent[3],
        }
        for _, gradient in ipairs(window._accentGrads) do
            if gradient.Parent then
                gradient.Color = accentSequence(window._accent)
            end
        end
    end

    function api.SetVisible(state)
        screen.Enabled = state == true
    end

    function api.Toggle()
        screen.Enabled = not screen.Enabled
    end

    function api.Minimize()
        setMinimized(window, true)
    end

    function api.Restore()
        setMinimized(window, false)
    end

    function api.Unload()
        unloadWindow(window)
    end
    api.Destroy = api.Unload

    api.Flags = CentuDox.Flags
    api.Screen = screen

    if options.dashboard ~= false then
        buildDashboard(window, type(options.dashboard) == "table" and options.dashboard or {})
    end

    return api
end

CentuDox.New = CentuDox.CreateWindow

function CentuDox.GetWindows()
    local result = {}
    for _, window in ipairs(windows) do
        table.insert(result, window._api)
    end
    return result
end

function CentuDox.UnloadAll()
    for index = #windows, 1, -1 do
        local window = windows[index]
        if window then
            unloadWindow(window)
        end
    end
end

function CentuDox.Notify(notification)
    local window = windows[#windows]
    if window then
        return notify(window, notification)
    end
    return nil
end

GENV.CentuDoxLibrary = CentuDox

return CentuDox
