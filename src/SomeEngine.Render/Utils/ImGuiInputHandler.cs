using System;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace SomeEngine.Render.Utils;

public class ImGuiInputHandler
{
    private readonly IInputContext _input;
    private readonly IWindow _window;

    public ImGuiInputHandler(IInputContext input, IWindow window)
    {
        _input = input;
        _window = window;

        var io = ImGui.GetIO();
        
        // Map keys
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }

        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnMouseScroll;
        }
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        ImGui.GetIO().AddInputCharacter(c);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int keyCode)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(MapKey(key), true);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int keyCode)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(MapKey(key), false);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        ImGui.GetIO().AddMouseButtonEvent((int)button, true);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        ImGui.GetIO().AddMouseButtonEvent((int)button, false);
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        ImGui.GetIO().AddMousePosEvent(pos.X, pos.Y);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel scroll)
    {
        ImGui.GetIO().AddMouseWheelEvent(scroll.X, scroll.Y);
    }

    public void Update(float dt)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_window.Size.X, _window.Size.Y);
        io.DeltaTime = dt;
    }

    private ImGuiKey MapKey(Key key)
    {
        return key switch
        {
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Space => ImGuiKey.Space,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.AltRight => ImGuiKey.RightAlt,
            _ => ImGuiKey.None
        };
    }
}
