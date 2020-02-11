using PoeShared.Scaffolding;

namespace MicSwitch.Services
{
    internal interface IComplexHotkeyTracker : IDisposableReactiveObject
    {
        bool IsActive { get; }
    }
}