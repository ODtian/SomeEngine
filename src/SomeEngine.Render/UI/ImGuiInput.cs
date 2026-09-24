using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace SomeEngine.Render.UI;

public sealed class ImGuiInput : IDisposable
{
    private readonly IInputContext _input;
    private readonly IWindow _window;
    private readonly IntPtr _context;
    private bool _disposed;

    public ImGuiInput(IInputContext input, IWindow window)
    {
        _input = input;
        _window = window;

        _context = ImGui.GetCurrentContext();
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("ImGui input requires an initialized ImGui context.");

        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }

        foreach (IMouse mouse in _input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnMouseScroll;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
            keyboard.KeyChar -= OnKeyChar;
        }

        foreach (IMouse mouse in _input.Mice)
        {
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.MouseMove -= OnMouseMove;
            mouse.Scroll -= OnMouseScroll;
        }

        _disposed = true;
    }

    public void Update(float dt)
    {
        ThrowIfDisposed();
        MakeCurrent();
        Update(dt, new Vector2(_window.Size.X, _window.Size.Y));
    }

    public void Update(float dt, uint displayWidth, uint displayHeight)
    {
        ThrowIfDisposed();
        MakeCurrent();
        Update(dt, new Vector2(displayWidth, displayHeight));
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddInputCharacter(c);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int keyCode)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddKeyEvent(MapKey(key), true);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int keyCode)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddKeyEvent(MapKey(key), false);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddMouseButtonEvent((int)button, true);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddMouseButtonEvent((int)button, false);
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddMousePosEvent(pos.X, pos.Y);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel scroll)
    {
        if (_disposed)
            return;

        MakeCurrent();
        ImGui.GetIO().AddMouseWheelEvent(scroll.X, scroll.Y);
    }

    private static void Update(float dt, Vector2 displaySize)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = displaySize;
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = dt;
    }

    private void MakeCurrent()
    {
        if (ImGui.GetCurrentContext() != _context)
            ImGui.SetCurrentContext(_context);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ImGuiInput));
    }

    private static ImGuiKey MapKey(Key key)
        => key switch
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
