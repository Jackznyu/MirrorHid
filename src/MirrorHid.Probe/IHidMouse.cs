namespace MirrorHid.Probe;

public interface IHidMouse : IAsyncDisposable
{
    bool IsAbsolutePointerSubscribed { get; }
    void StartAdvertising();
    Task SendAsync(byte buttons, int x, int y, int wheel);
    Task SendAbsoluteAsync(byte buttons, ushort x, ushort y, int wheel);
}
