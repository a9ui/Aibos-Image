using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PhotoViewer.Wpf;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string NamePrefix = "Local\\AibosImage.Wpf.SingleInstance.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceCoordinator(string identity)
    {
        string suffix = HashIdentity(identity);
        _mutex = new Mutex(
            initiallyOwned: true,
            $"{NamePrefix}.Mutex.{suffix}",
            out bool createdNew);
        _ownsMutex = createdNew;
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $"{NamePrefix}.Activate.{suffix}");
    }

    internal bool IsPrimary => _ownsMutex;

    internal static SingleInstanceCoordinator CreateForCurrentUser()
    {
        string identity = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException(
                "The current Windows user identity is unavailable.");
        return new SingleInstanceCoordinator(identity);
    }

    internal static SingleInstanceCoordinator CreateForSmoke(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("A smoke identity is required.", nameof(identity));
        return new SingleInstanceCoordinator("smoke-" + identity);
    }

    internal void StartListening(Action activate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activate);
        if (!IsPrimary)
            throw new InvalidOperationException(
                "Only the primary Aibos Image instance can listen for activation.");
        if (_activationRegistration is not null)
            return;

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut && state is Action callback)
                    callback();
            },
            activate,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    internal bool SignalPrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
            return false;
        return _activationEvent.Set();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }

    private static string HashIdentity(string identity)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity)))[..24];
}
