using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using JetBrains.Annotations;
using log4net;
using MicSwitch.MainWindow.Models;
using MicSwitch.Modularity;
using Mono.Collections.Generic;
using PoeShared;
using PoeShared.Modularity;
using PoeShared.Native;
using PoeShared.Prism;
using PoeShared.Scaffolding;
using PoeShared.Scaffolding.WPF;
using PoeShared.UI.Hotkeys;
using ReactiveUI;

namespace MicSwitch.Services
{
    internal sealed class ComplexHotkeyTracker : DisposableReactiveObject, IComplexHotkeyTracker
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ComplexHotkeyTracker));
        private static readonly Process CurrentProcess = Process.GetCurrentProcess();

        private readonly IHotkeyConverter hotkeyConverter;
        private readonly IConfigProvider<MicSwitchHotkeyConfig> configProvider;
        private readonly IHotkeyTracker hotkeyTracker;
        private HookForm hookForm;

        public ComplexHotkeyTracker(
            [NotNull] IHotkeyConverter hotkeyConverter,
            [NotNull] IConfigProvider<MicSwitchHotkeyConfig> configProvider,
            [NotNull] IFactory<IHotkeyTracker> hotkeyTrackerFactory)
        {
            this.hotkeyConverter = hotkeyConverter;
            this.configProvider = configProvider;
            this.hotkeyTracker = hotkeyTrackerFactory.Create();
            Log.Debug($"Scheduling HotkeyTracker initialization using background scheduler");
            this.RaiseWhenSourceValue(x => x.IsActive, hotkeyTracker, x => x.IsActive).AddTo(Anchors);
            
            var bgThread = new Thread(x => Initialize())
            {
                IsBackground = true, 
                ApartmentState = ApartmentState.STA, 
                Name = "HotkeyTracker"
            };
            bgThread.Start();
        }

        public bool IsActive => hotkeyTracker.IsActive;
        
        private void Initialize()
        {
            Log.Debug($"Initializing HotkeyTracker");

            configProvider.WhenChanged
                .SubscribeSafe(
                    () =>
                    {
                        var actualConfig = configProvider.ActualConfig;
                        hotkeyTracker.Clear();
                        
                        try
                        {
                            hotkeyTracker.Add(hotkeyConverter.ConvertFromString(actualConfig.Hotkey.Key));
                            hotkeyTracker.Add(hotkeyConverter.ConvertFromString(actualConfig.Hotkey.AlternativeKey));
                        }
                        catch (Exception e)
                        {
                            Log.Error($"Failed to parse config hotkeys: {configProvider.ActualConfig.Hotkey}", e);
                        }

                        hotkeyTracker.HotkeyMode = actualConfig.MuteMode == MuteMode.PushToMute || actualConfig.MuteMode == MuteMode.PushToTalk
                            ? HotkeyMode.Hold
                            : HotkeyMode.Click;
                        hotkeyTracker.SuppressKey = actualConfig.Hotkey.Suppress;
                    }, Log.HandleUiException)
                .AddTo(Anchors);

            try
            {
                Log.Debug($"Creating form for hooking keyboard and mouse events, process main window: {CurrentProcess.MainWindowTitle} {CurrentProcess.MainWindowHandle}");
                hookForm = new HookForm();
                Log.Debug($"Running message loop in hook form");
                var result = hookForm.ShowDialog();
                Log.Debug($"Message loop terminated gracefully, dialog result: {result}");
            }
            catch (Exception e)
            {
                Log.HandleUiException(new ApplicationException("Exception occurred in Complex Hotkey message loop", e));
            }
            finally
            {
                Log.Debug($"Hook form thread terminated");
            }
        }

        private sealed class HookForm : Window
        {
            private readonly CompositeDisposable anchors = new CompositeDisposable();

            public HookForm()
            {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                Title = $"{assembly.GetName().Name} {assembly.GetName().Version} {nameof(HookForm)}";
                ShowInTaskbar = false;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Width = 0;
                Height = 0;
                this.Loaded += OnLoaded;
                Log.Info("HookForm created");

                this.LogWndProc("HookForm").AddTo(anchors);
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                this.Loaded -= OnLoaded;

                Log.Info("HookForm loaded, applying style...");
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                Log.Debug($"HookForm handle: {hwnd.ToHexadecimal()}");
                UnsafeNative.HideSystemMenu(hwnd);
                UnsafeNative.SetWindowExTransparent(hwnd);
                UnsafeNative.SetWindowRgn(hwnd, Rectangle.Empty);
                Log.Info("HookForm successfully initialized");
            }

            protected override void OnClosed(EventArgs e)
            {
                base.OnClosed(e);
                anchors.Dispose();
            }
        }
    }
}