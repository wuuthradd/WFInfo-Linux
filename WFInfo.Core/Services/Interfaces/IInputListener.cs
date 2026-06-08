using System;
using WFInfo.Models;

namespace WFInfo.Services
{
    public class KeyEventArgs : EventArgs
    {
        public VirtualKey Key { get; set; }
        public bool IsDown { get; set; }
    }

    public class MouseEventArgs : EventArgs
    {
        public VirtualMouseButton Button { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public interface IInputListener : IDisposable
    {
        event EventHandler<KeyEventArgs> KeyEvent;
        event EventHandler<MouseEventArgs> MouseEvent;
        bool IsKeyHeld(VirtualKey key) => false;
        string StartupWarning => null;
    }
}