using NAudio.Wave;
using System;
using System.IO;
using System.Management; // WMI
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications.Management; // UserNotificationListener
using Windows.UI.Notifications; // UserNotification
using System.Windows.Forms;
using System.Windows.Media.Effects; // Screen

namespace WinIsland
{
    public partial class MainWindow : Window
    {
        private bool _isFileStationActive = false;
        private List<string> _storedFiles = new List<string>();
        private Spring _widthSpring;
        private Spring _heightSpring;
        private DateTime _lastFrameTime;

        private GlobalSystemMediaTransportControlsSessionManager _mediaManager;
        private GlobalSystemMediaTransportControlsSession _currentSession;
        private WasapiLoopbackCapture _capture;
        private float _currentVolume = 0;

        // 通知相关
        private DispatcherTimer _notificationTimer;
        private bool _isNotificationActive = false;
        private UserNotificationListener _listener;

        public MainWindow()
        {
            InitializeComponent();
            
            // 隐藏在 Alt+Tab 切换器中
            this.Loaded += (s, e) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
           
                  Task.Delay(100).ContinueWith(_ => Dispatcher.Invoke(() => SetClickThrough(true)));
            };
            
            // 窗口内容渲染完成后居中
            this.ContentRendered += (s, e) =>
            {
                CenterWindowAtTop();
            };
            
            InitializePhysics();
            InitializeMediaListener();
            InitializeAudioCapture();
            InitializeDeviceWatcher();
            InitializeNotificationTimer();
            InitializeNotificationListener();
            InitializeDrinkWaterFeature();
            InitializeTodoFeature();
        }

        public void ReloadSettings()
        {
            InitializeDrinkWaterFeature();
            InitializeTodoFeature();
        }

        private void CenterWindowAtTop()
        {
            try
            {
                // 获取主屏幕的工作区域
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                
                // 计算居中位置
                this.Left = (screenWidth - this.Width) / 2;
                this.Top = 10; // 距离顶部 10 像素
                
                LogDebug($"Window centered: Left={this.Left}, Top={this.Top}, Width={this.Width}, ScreenWidth={screenWidth}");
            }
            catch (Exception ex)
            {
                LogDebug($"Center window error: {ex.Message}");
            }
        }

        // Win32 API 声明
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        private void SetClickThrough(bool enable)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (enable)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
                    LogDebug("Click-through enabled");
                }
                else
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
                    LogDebug("Click-through disabled");
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        #region 0. 通知逻辑 (WMI & UserNotificationListener)

        private void InitializeNotificationTimer()
        {
            _notificationTimer = new DispatcherTimer();
            _notificationTimer.Interval = TimeSpan.FromSeconds(3); // 通知显示3秒
            _notificationTimer.Tick += (s, e) => HideNotification();
        }

        private async void InitializeNotificationListener()
        {
            try
            {
                if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener")) return;

                _listener = UserNotificationListener.Current;
                var accessStatus = await _listener.RequestAccessAsync();

                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    _listener.NotificationChanged += Listener_NotificationChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Notification Listener Error: {ex.Message}");
            }
        }

        private void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                // 暂时移除 ChangeType 检查
                var notifId = args.UserNotificationId;
                var notif = _listener.GetNotification(notifId);
                if (notif == null) return;

                var appName = notif.AppInfo.DisplayInfo.DisplayName;
                if (string.IsNullOrEmpty(appName)) return;

                // 简单的过滤逻辑: 微信 或 QQ
                bool isWeChat = appName.Contains("WeChat", StringComparison.OrdinalIgnoreCase) || appName.Contains("微信");
                bool isQQ = appName.Contains("QQ", StringComparison.OrdinalIgnoreCase);

                if (isWeChat || isQQ)
                {
                    string displayMsg = $"{appName}: New Message";

                    // 尝试获取详细内容
                    try
                    {
                        var binding = notif.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                        if (binding != null)
                        {
                            var texts = binding.GetTextElements();
                            string title = texts.Count > 0 ? texts[0].Text : appName;
                            string body = texts.Count > 1 ? texts[1].Text : "New Message";
                            displayMsg = $"{title}: {body}";
                        }
                    }
                    catch { }
                    
                    Dispatcher.Invoke(() => ShowMessageNotification(displayMsg));
                }
            }
            catch { }
        }

        // 使用 Windows.Devices.Enumeration 替代 WMI，更加轻量且实时
        private Windows.Devices.Enumeration.DeviceWatcher _bluetoothWatcher;
        private Windows.Devices.Enumeration.DeviceWatcher _usbWatcher;
        private System.Collections.Concurrent.ConcurrentDictionary<string, string> _deviceMap = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        private System.Collections.Concurrent.ConcurrentDictionary<string, (bool isConnected, DateTime lastUpdate)> _deviceStateCache = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool, DateTime)>();
        private bool _isBluetoothEnumComplete = false;
        private bool _isUsbEnumComplete = false;

        private void InitializeDeviceWatcher()
        {
            try
            {
                LogDebug("Initializing Device Watchers...");
                
                // 蓝牙设备监听 (配对的蓝牙设备)
                string bluetoothSelector = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
                
                // 请求额外属性，特别是连接状态
                var requestedProperties = new string[]
                {
                    "System.Devices.Aep.IsConnected",
                    "System.Devices.Aep.SignalStrength",
                    "System.Devices.Aep.Bluetooth.Le.IsConnectable"
                };
                
                _bluetoothWatcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
                    bluetoothSelector,
                    requestedProperties,
                    Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);

                _bluetoothWatcher.Added += BluetoothWatcher_Added;
                _bluetoothWatcher.Removed += BluetoothWatcher_Removed;
                _bluetoothWatcher.Updated += BluetoothWatcher_Updated;
                _bluetoothWatcher.EnumerationCompleted += (s, e) => 
                { 
                    _isBluetoothEnumComplete = true;
                    LogDebug("Bluetooth enumeration completed");
                };
                _bluetoothWatcher.Start();
                LogDebug("Bluetooth watcher started");

                // USB 设备监听
                string usbSelector = "System.Devices.InterfaceClassGuid:=\"{a5dcbf10-6530-11d2-901f-00c04fb951ed}\""; // USB 设备接口
                _usbWatcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
                    usbSelector,
                    null,
                    Windows.Devices.Enumeration.DeviceInformationKind.DeviceInterface);

                _usbWatcher.Added += UsbWatcher_Added;
                _usbWatcher.Removed += UsbWatcher_Removed;
                _usbWatcher.EnumerationCompleted += (s, e) => { _isUsbEnumComplete = true; };
                _usbWatcher.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Device Watcher Error: {ex.Message}");
            }
        }

        private void BluetoothWatcher_Added(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformation args)
        {
            LogDebug($"BT Added: {args.Name} (ID: {args.Id.Substring(0, Math.Min(30, args.Id.Length))})");
            
            // 过滤无效设备名
            if (string.IsNullOrEmpty(args.Name) || !IsValidDeviceName(args.Name))
            {
                LogDebug($"BT Added: Filtered invalid name: {args.Name}");
                return;
            }
            
            _deviceMap.TryAdd(args.Id, args.Name);
            
            // 初始化设备状态缓存（假设初始为断开）
            if (args.Properties.ContainsKey("System.Devices.Aep.IsConnected"))
            {
                bool isConnected = (bool)args.Properties["System.Devices.Aep.IsConnected"];
                _deviceStateCache[args.Id] = (isConnected, DateTime.Now);
                LogDebug($"BT Added: Initial state = {isConnected}");
            }

            // 只在枚举完成后且设备是连接状态才显示通知
            if (_isBluetoothEnumComplete && args.Properties.ContainsKey("System.Devices.Aep.IsConnected"))
            {
                bool isConnected = (bool)args.Properties["System.Devices.Aep.IsConnected"];
                if (isConnected)
                {
                    LogDebug($"BT Added Notification: {args.Name}");
                    Dispatcher.Invoke(() => ShowDeviceNotification($"蓝牙: {args.Name}", true));
                }
            }
        }

        private void BluetoothWatcher_Removed(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformationUpdate args)
        {
            LogDebug($"BT Removed: ID={args.Id.Substring(0, Math.Min(30, args.Id.Length))}");
            
            // 检查该设备之前的连接状态
            bool wasConnected = false;
            if (_deviceStateCache.TryRemove(args.Id, out var lastState))
            {
                wasConnected = lastState.isConnected;
            }

            if (_deviceMap.TryRemove(args.Id, out string deviceName))
            {
                LogDebug($"BT Removed from map: {deviceName}");
                // 只有当枚举完成，且设备之前确实是连接状态时，才显示断开通知
                // 这样避免了未连接的配对设备在系统后台刷新时触发误报
                if (_isBluetoothEnumComplete && !string.IsNullOrEmpty(deviceName) && wasConnected)
                {
                    LogDebug($"BT Removed Notification: {deviceName}");
                    Dispatcher.Invoke(() => ShowDeviceNotification($"蓝牙: {deviceName}", false));
                }
            }
        }

        private void BluetoothWatcher_Updated(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformationUpdate args)
        {
            // 蓝牙设备状态更新（连接/断开）
            LogDebug($"BT Updated: ID={args.Id.Substring(0, Math.Min(30, args.Id.Length))}, Props={args.Properties.Count}");
            
            if (args.Properties.ContainsKey("System.Devices.Aep.IsConnected"))
            {
                bool isConnected = (bool)args.Properties["System.Devices.Aep.IsConnected"];
                LogDebug($"BT IsConnected: {isConnected}");
                
                if (_deviceMap.TryGetValue(args.Id, out string deviceName) && !string.IsNullOrEmpty(deviceName))
                {
                    // 过滤无效设备名
                    if (!IsValidDeviceName(deviceName))
                    {
                        LogDebug($"BT Updated: Filtered invalid name: {deviceName}");
                        return;
                    }
                    
                    // 防抖：检查状态是否真的改变
                    var now = DateTime.Now;
                    bool shouldNotify = false;
                    
                    if (_deviceStateCache.TryGetValue(args.Id, out var cachedState))
                    {
                        // 状态没变，忽略
                        if (cachedState.isConnected == isConnected)
                        {
                            LogDebug($"BT Updated: State unchanged, ignored");
                            return;
                        }
                        
                        // 距离上次更新太近（2秒内），忽略
                        if ((now - cachedState.lastUpdate).TotalSeconds < 2)
                        {
                            LogDebug($"BT Updated: Too frequent, ignored (last: {(now - cachedState.lastUpdate).TotalSeconds:F1}s ago)");
                            return;
                        }
                        
                        // 状态真的改变了，且时间间隔足够
                        shouldNotify = true;
                    }
                    else
                    {
                        // 第一次收到这个设备的状态，只在连接时通知
                        shouldNotify = isConnected;
                    }
                    
                    // 更新缓存
                    _deviceStateCache[args.Id] = (isConnected, now);
                    
                    if (shouldNotify)
                    {
                        LogDebug($"BT Updated Notification: {deviceName} -> {(isConnected ? "Connected" : "Disconnected")}");
                        Dispatcher.Invoke(() => ShowDeviceNotification($"蓝牙: {deviceName}", isConnected));
                    }
                }
                else
                {
                    LogDebug($"BT device not in map or empty name");
                }
            }
        }

        private bool IsValidDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            
            // 过滤太短的名字（可能是 MAC 地址片段）
            if (name.Length < 4) return false;
            
            // 过滤纯数字或纯字母+数字组合（如 A077, 1234）
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z0-9]{4,6}$"))
            {
                LogDebug($"Filtered MAC-like name: {name}");
                return false;
            }
            
            // 过滤包含特殊字符的设备ID
            if (name.Contains("\\") || name.Contains("{") || name.Contains("}"))
            {
                return false;
            }
            
            return true;
        }

        private void LogDebug(string message)
        {
            try
            {
                string logPath = "debug_log.txt";
                string logMessage = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss}: {message}\n";
                File.AppendAllText(logPath, logMessage);
            }
            catch { }
        }

        private void UsbWatcher_Added(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformation args)
        {
            _deviceMap.TryAdd(args.Id, args.Name);

            if (_isUsbEnumComplete && !string.IsNullOrEmpty(args.Name))
            {
                Dispatcher.Invoke(() => ShowDeviceNotification($"USB: {args.Name}", true));
            }
        }

        private void UsbWatcher_Removed(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformationUpdate args)
        {
            if (_deviceMap.TryRemove(args.Id, out string deviceName))
            {
                if (_isUsbEnumComplete && !string.IsNullOrEmpty(deviceName))
                {
                    Dispatcher.Invoke(() => ShowDeviceNotification($"USB: {deviceName}", false));
                }
            }
        }

        private void ShowDeviceNotification(string deviceName, bool isConnected)
        {
            ActivateNotification();
                    

            NotificationText.Text = deviceName;
            
            if (isConnected)
            {
                IconConnect.Visibility = Visibility.Visible;
                IconDisconnect.Visibility = Visibility.Collapsed;
                IconMessage.Visibility = Visibility.Collapsed;
                NotificationText.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 204)); // Green
            }
            else
            {
                IconConnect.Visibility = Visibility.Collapsed;
                IconDisconnect.Visibility = Visibility.Visible;
                IconMessage.Visibility = Visibility.Collapsed;
                NotificationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 51, 51)); // Red
            }

            PlayFlipAnimation();
        }

        private void ShowMessageNotification(string message)
        {
            ActivateNotification();

            NotificationText.Text = message;
            
            IconConnect.Visibility = Visibility.Collapsed;
            IconDisconnect.Visibility = Visibility.Collapsed;
            IconMessage.Visibility = Visibility.Visible;
            NotificationText.Foreground = new SolidColorBrush(Color.FromRgb(0, 191, 255)); // DeepSkyBlue

            PlayFlipAnimation();
        }

        private void ActivateNotification()
        {
            _isNotificationActive = true;
            _notificationTimer.Stop();
            _notificationTimer.Start();

            // 隐藏其他内容
            AlbumCover.Visibility = Visibility.Collapsed;
            SongTitle.Visibility = Visibility.Collapsed;
            ControlPanel.Visibility = Visibility.Collapsed;
            VisualizerContainer.Visibility = Visibility.Collapsed;

            // 显示通知面板
            NotificationPanel.Visibility = Visibility.Visible;
            NotificationPanel.Opacity = 0;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;
            FileStationPanel.Visibility = Visibility.Collapsed;

            DynamicIsland.IsHitTestVisible = true; // 允许鼠标交互
            SetClickThrough(false); // 激活通知时允许交互
            
            // 清除动画锁定，确保 1.0 生效
            DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
            DynamicIsland.Opacity = 1.0;

            // 设定通知尺寸
            _widthSpring.Target = 320;
            _heightSpring.Target = 50;

            // 内容淡入动画
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400),
                BeginTime = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NotificationPanel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void PlayFlipAnimation()
        {
            // 3D 翻转效果动画
            var flipAnimation = new DoubleAnimationUsingKeyFrames();
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
            });
            flipAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            var scaleYAnimation = new DoubleAnimationUsingKeyFrames();
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            NotificationIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, flipAnimation);
            NotificationIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
        }

        private void HideNotification()
        {
            _isNotificationActive = false;
            _notificationTimer.Stop();

            // 淡出动画
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Collapsed;
                TodoPanel.Visibility = Visibility.Collapsed;
                
                // 恢复之前的状态
                CheckCurrentSession();
            };

            if (NotificationPanel.Visibility == Visibility.Visible)
                NotificationPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            else if (DrinkWaterPanel.Visibility == Visibility.Visible)
                DrinkWaterPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            else if (TodoPanel.Visibility == Visibility.Visible)
                TodoPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            else
            {
                // 如果没有可见的面板，直接恢复状态
                CheckCurrentSession();
            }
        }

        #endregion

        #region Drink Water Notification
        
        private DispatcherTimer _drinkWaterScheduler;
        private DateTime _nextDrinkTime;
        private AppSettings _settings;

        private void InitializeDrinkWaterFeature()
        {
            _settings = AppSettings.Load();
            
            if (_drinkWaterScheduler == null)
            {
                _drinkWaterScheduler = new DispatcherTimer();
                _drinkWaterScheduler.Interval = TimeSpan.FromSeconds(30); // 每30秒检查一次
                _drinkWaterScheduler.Tick += DrinkWaterScheduler_Tick;
            }

            if (_settings.DrinkWaterEnabled)
            {
                // 如果是首次启用或重新启用，设置下一次提醒时间
                if (!_drinkWaterScheduler.IsEnabled)
                {
                    ResetNextDrinkTime();
                    _drinkWaterScheduler.Start();
                }
            }
            else
            {
                _drinkWaterScheduler.Stop();
            }
        }

        private void ResetNextDrinkTime()
        {
            _nextDrinkTime = DateTime.Now.AddMinutes(_settings.DrinkWaterIntervalMinutes);
            LogDebug($"Next drink time set to: {_nextDrinkTime}");
        }

        private string _lastTriggeredCustomTime = "";

                private void DrinkWaterScheduler_Tick(object sender, EventArgs e)
        {
            if (!_settings.DrinkWaterEnabled) return;

            if (_settings.DrinkWaterMode == DrinkWaterMode.Interval)
            {
                // 检查是否在活动时间段内
                if (!IsWithinActiveHours()) return;

                if (DateTime.Now >= _nextDrinkTime)
                {
                    // 该提醒了！
                    ShowDrinkWaterNotification();
                    ResetNextDrinkTime(); 
                }
            }
            else // 自定义模式
            {
                var nowStr = DateTime.Now.ToString("HH:mm");
                if (_settings.CustomDrinkWaterTimes != null && _settings.CustomDrinkWaterTimes.Contains(nowStr))
                {
                    // 防止同一分钟内重复提醒
                    if (_lastTriggeredCustomTime != nowStr)
                    {
                        ShowDrinkWaterNotification();
                        _lastTriggeredCustomTime = nowStr;
                    }
                }
            }
        }

        private bool IsWithinActiveHours()
        {
            try
            {
                if (TimeSpan.TryParse(_settings.DrinkWaterStartTime, out TimeSpan start) &&
                    TimeSpan.TryParse(_settings.DrinkWaterEndTime, out TimeSpan end))
                {
                    var now = DateTime.Now.TimeOfDay;
                    if (start <= end)
                    {
                        return now >= start && now <= end;
                    }
                    else
                    {
                        // 跨午夜 (例如 22:00 到 06:00)
                        return now >= start || now <= end;
                    }
                }
            }
            catch { }
            return true; // 如果解析失败默认为 true
        }

        private void ShowDrinkWaterNotification()
        {
            Dispatcher.Invoke(() =>
            {
                _isNotificationActive = true;
                _notificationTimer.Stop(); // 停止其他通知的自动隐藏计时器

                // 隐藏其他内容
                AlbumCover.Visibility = Visibility.Collapsed;
                SongTitle.Visibility = Visibility.Collapsed;
                ControlPanel.Visibility = Visibility.Collapsed;
                VisualizerContainer.Visibility = Visibility.Collapsed;
                NotificationPanel.Visibility = Visibility.Collapsed;
                TodoPanel.Visibility = Visibility.Collapsed;
                FileStationPanel.Visibility = Visibility.Collapsed;

                // 显示喝水提醒
                DrinkWaterPanel.Visibility = Visibility.Visible;
                DrinkWaterPanel.Opacity = 0;

                DynamicIsland.IsHitTestVisible = true; // 允许鼠标交互
                SetClickThrough(false);
                
                // 清除动画锁定
                DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                DynamicIsland.Opacity = 1.0;

                // 展开动画 (更宽一点)
                _widthSpring.Target = 280;
                _heightSpring.Target = 50;

                // 岛屿发光脉冲 (蓝色)
                PlayIslandGlowEffect(Colors.DeepSkyBlue);

                // 内容进场 (上浮淡入)
                PlayContentEntranceAnimation(DrinkWaterPanel);

                // 水滴动画 (优化版)
                PlayWaterDropAnimation();
            });
        }

        private void PlayWaterDropAnimation()
        {
            // 水滴悬浮动画
            var floatAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            floatAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            floatAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))) 
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            floatAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))) 
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

            WaterIconTranslate?.BeginAnimation(TranslateTransform.YProperty, floatAnim);
        }

        private void BtnDrank_Click(object sender, RoutedEventArgs e)
        {
            // 用户已确认
            HideNotification(); // 复用隐藏逻辑来重置状态
            ResetNextDrinkTime(); // 重置周期
        }

        #endregion

        #region Todo Notification

        private DispatcherTimer _todoScheduler;
        private TodoItem _currentTodoItem;

        private void InitializeTodoFeature()
        {
            _settings = AppSettings.Load(); // 确保是最新的设置
            
            if (_todoScheduler == null)
            {
                _todoScheduler = new DispatcherTimer();
                _todoScheduler.Interval = TimeSpan.FromSeconds(15); // 每15秒检查一次
                _todoScheduler.Tick += TodoScheduler_Tick;
            }

            if (_settings.TodoEnabled)
            {
                if (!_todoScheduler.IsEnabled) _todoScheduler.Start();
            }
            else
            {
                _todoScheduler.Stop();
            }
        }

        private void TodoScheduler_Tick(object sender, EventArgs e)
        {
            if (!_settings.TodoEnabled || _settings.TodoList == null) return;

            var now = DateTime.Now;
            
            foreach (var item in _settings.TodoList)
            {
                if (!item.IsCompleted && item.ReminderTime <= now)
                {
                    // 找到了！
                    _currentTodoItem = item;
                    ShowTodoNotification(item);
                    
                    if (_isNotificationActive && TxtTodoMessage.Text == item.Content) return;
                    
                    break; // 一次只显示一个
                }
            }
        }

        private void ShowTodoNotification(TodoItem item)
        {
            Dispatcher.Invoke(() =>
            {
                _isNotificationActive = true;
                _notificationTimer.Stop();

                AlbumCover.Visibility = Visibility.Collapsed;
                SongTitle.Visibility = Visibility.Collapsed;
                ControlPanel.Visibility = Visibility.Collapsed;
                VisualizerContainer.Visibility = Visibility.Collapsed;
                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Collapsed;
                FileStationPanel.Visibility = Visibility.Collapsed;

                TodoPanel.Visibility = Visibility.Visible;
                TodoPanel.Opacity = 0;
                TxtTodoMessage.Text = item.Content;

                DynamicIsland.IsHitTestVisible = true; 
                SetClickThrough(false);
                
                DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                DynamicIsland.Opacity = 1.0;

                // 展开动画 (稍微更宽)
                _widthSpring.Target = 320;
                _heightSpring.Target = 50;

                // 岛屿发光脉冲 (橙色)
                PlayIslandGlowEffect(Colors.Orange);

                // 内容进场
                PlayContentEntranceAnimation(TodoPanel);

                // 图标动画
                PlayTodoIconAnimation();
            });
        }

        private void PlayTodoIconAnimation()
        {
            var rotateAnim = new DoubleAnimationUsingKeyFrames{ RepeatBehavior = RepeatBehavior.Forever };
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))) { EasingFunction = new SineEase() });
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))) { EasingFunction = new SineEase() });
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))) { EasingFunction = new SineEase() });
            rotateAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));

            TodoIconRotate?.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
        }

        private void PlayContentEntranceAnimation(FrameworkElement element)
        {
            // 确保 RenderTransform 准备就绪
            TranslateTransform translate = null;
            if (element.RenderTransform is TranslateTransform tt)
            {
                translate = tt;
            }
            else if (element.RenderTransform is TransformGroup tg)
            {
                foreach(var t in tg.Children) if(t is TranslateTransform) translate = t as TranslateTransform;
            }
            
            if (translate == null)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            // 重置状态
            translate.Y = 20;
            element.Opacity = 0;

            // 淡入
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            // 上浮
            var slideUp = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            };

            element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        private void PlayIslandGlowEffect(Color glowColor)
        {
            if (DynamicIsland.Effect is DropShadowEffect shadow)
            {
                var colorAnim = new ColorAnimation(glowColor, TimeSpan.FromMilliseconds(300))
                {
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(2),
                    FillBehavior = FillBehavior.Stop
                };

                // 稍微扩散一下 Shadow
                var blurAnim = new DoubleAnimation(shadow.BlurRadius, 30, TimeSpan.FromMilliseconds(300))
                {
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(2),
                    FillBehavior = FillBehavior.Stop
                };

                shadow.BeginAnimation(DropShadowEffect.ColorProperty, colorAnim);
                shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            }
        }

        private void BtnTodoDone_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTodoItem != null)
            {
                _currentTodoItem.IsCompleted = true;
                _settings.Save();
                _currentTodoItem = null;
            }
            
            HideNotification();
        }

        #endregion

        #region 1. 按钮控制逻辑

        private async void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TrySkipPreviousAsync(); } catch { CheckCurrentSession(); }
        }

        private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TryTogglePlayPauseAsync(); } catch { CheckCurrentSession(); }
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TrySkipNextAsync(); } catch { CheckCurrentSession(); }
        }

        #endregion

        #region 3. 媒体信息
        private async void InitializeMediaListener()
        {
            try
            {
                _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mediaManager.CurrentSessionChanged += (s, e) => CheckCurrentSession();
                CheckCurrentSession();
            }
            catch { }
        }

        private void CheckCurrentSession()
        {
            // 如果正在显示通知，不要打断它，等通知结束后会自动调用此方法
            if (_isNotificationActive) return;

            // 如果正在进行文件拖放或有文件存储，不要打断
            if (_isFileStationActive) return;
            
            // 如果文件中转站有文件，优先显示中转站
            if (_storedFiles.Count > 0) 
            {
                ShowFileStationState();
                return;
            }

            try
            {
                var session = _mediaManager.GetCurrentSession();
                if (session != null)
                {
                    _currentSession = session;
                    _currentSession.MediaPropertiesChanged += async (s, e) => await UpdateMediaInfo(s);
                    _currentSession.PlaybackInfoChanged += (s, e) => UpdatePlaybackStatus(s);
                    var t = UpdateMediaInfo(_currentSession);
                    UpdatePlaybackStatus(_currentSession);
                }
                else
                {
                    EnterStandbyMode();
                }
            }
            catch
            {
                EnterStandbyMode();
            }
        }

        private void EnterStandbyMode()
        {
            _currentSession = null;
            _widthSpring.Target = 120;
            _heightSpring.Target = 35;
            
            Dispatcher.Invoke(() =>
            {
                ControlPanel.Visibility = Visibility.Collapsed;
                VisualizerContainer.Visibility = Visibility.Collapsed;
                AlbumCover.Visibility = Visibility.Collapsed;
                SongTitle.Visibility = Visibility.Visible; // 确保控件存在
                SongTitle.Text = ""; 
                NotificationPanel.Visibility = Visibility.Collapsed;
                DrinkWaterPanel.Visibility = Visibility.Collapsed;
                TodoPanel.Visibility = Visibility.Collapsed;
                FileStationPanel.Visibility = Visibility.Collapsed;
                
                // 启用交互以支持文件拖放
                DynamicIsland.IsHitTestVisible = true;
                SetClickThrough(false); 
                
                // 清除可能存在的动画锁定，确保透明度设置生效
                DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                DynamicIsland.Opacity = 0.4; // 待机透明度
            });
          
        }

        private async Task UpdateMediaInfo(GlobalSystemMediaTransportControlsSession session)
        {
            if (_isNotificationActive) return;

            try
            {
                var info = await session.TryGetMediaPropertiesAsync();
                Dispatcher.Invoke(() =>
                {
                    if (info != null)
                    {
                        var oldTargetW = _widthSpring.Target;
                        _widthSpring.Target = 400;
                        _heightSpring.Target = 60;
                        
                        // 如果尺寸发生变化，重置最后帧时间以获得平滑过渡
                        if(Math.Abs(oldTargetW - 400) > 1) _lastFrameTime = DateTime.Now;

                        SongTitle.Visibility = Visibility.Visible;
                        SongTitle.Text = info.Title;

                        AlbumCover.Visibility = Visibility.Visible;
                        VisualizerContainer.Visibility = Visibility.Visible;
                        ControlPanel.Visibility = Visibility.Visible;
                        NotificationPanel.Visibility = Visibility.Collapsed;
                        DrinkWaterPanel.Visibility = Visibility.Collapsed;
                        TodoPanel.Visibility = Visibility.Collapsed;
                        FileStationPanel.Visibility = Visibility.Collapsed;

                        DynamicIsland.IsHitTestVisible = true; // 允许鼠标交互
                        SetClickThrough(false); // 媒体播放时允许交互
                        
                        // 清除动画锁定
                        DynamicIsland.BeginAnimation(UIElement.OpacityProperty, null);
                        DynamicIsland.Opacity = 1.0;

                        if (info.Thumbnail != null) LoadThumbnail(info.Thumbnail);
                    }
                });
            }
            catch { CheckCurrentSession(); }
        }
        #endregion

        private void UpdatePlaybackStatus(GlobalSystemMediaTransportControlsSession session)
        {
            if (_isNotificationActive) return;
            try
            {
                var info = session.GetPlaybackInfo();
                if (info == null) return;

                Dispatcher.Invoke(() =>
                {
                    if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        IconPlay.Visibility = Visibility.Collapsed;
                        IconPause.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        IconPlay.Visibility = Visibility.Visible;
                        IconPause.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch { }
        }

        #region 4. 物理与渲染
        private void InitializePhysics()
        {
            _widthSpring = new Spring(120);
            _heightSpring = new Spring(35);
            _lastFrameTime = DateTime.Now;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (DynamicIsland == null) return;

            var now = DateTime.Now;
            var dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            if (dt > 0.05) dt = 0.05;

            var newWidth = _widthSpring.Update(dt);
            var newHeight = _heightSpring.Update(dt);

            DynamicIsland.Width = Math.Max(1, newWidth);
            DynamicIsland.Height = Math.Max(1, newHeight);

            if (DynamicIsland.Height > 0)
                DynamicIsland.CornerRadius = new CornerRadius(DynamicIsland.Height / 2);

            if (Bar1 != null && VisualizerContainer.Visibility == Visibility.Visible) 
                UpdateVisualizer();
        }

        private void InitializeAudioCapture()
        {
            try { _capture = new WasapiLoopbackCapture(); _capture.DataAvailable += OnAudioDataAvailable; _capture.StartRecording(); } catch { }
        }
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            float max = 0;
            for (int i = 0; i < e.BytesRecorded; i += 8)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                var normalized = Math.Abs(sample / 32768f);
                if (normalized > max) max = normalized;
            }
            _currentVolume = max;
        }
        private void UpdateVisualizer()
        {
            var time = DateTime.Now.TimeOfDay.TotalSeconds;
            double baseH = 4 + (_currentVolume * 40);

            double h3 = Math.Max(4, baseH * (0.9 + 0.3 * Math.Sin(time * 20)));
            double h2 = Math.Max(4, baseH * (0.7 + 0.25 * Math.Cos(time * 18 + 1)));
            double h4 = Math.Max(4, baseH * (0.7 + 0.25 * Math.Cos(time * 16 + 2)));
            double h1 = Math.Max(4, baseH * (0.5 + 0.2 * Math.Sin(time * 14 + 3)));
            double h5 = Math.Max(4, baseH * (0.5 + 0.2 * Math.Sin(time * 12 + 4)));

            Bar1.Height = h1;
            Bar2.Height = h2;
            Bar3.Height = h3;
            Bar4.Height = h4;
            Bar5.Height = h5;
        }
        private async void LoadThumbnail(IRandomAccessStreamReference thumbnail)
        {
            try { var s = await thumbnail.OpenReadAsync(); var b = new BitmapImage(); b.BeginInit(); b.StreamSource = s.AsStream(); b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); AlbumCover.Source = b; } catch { }
        }
        private void DynamicIsland_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) 
        { 
            // 只有按住 Ctrl 键时才能拖动
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && 
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) 
            {
                DragMove(); 
            }
        }
        private void DynamicIsland_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 鼠标悬停时保持不透明，防止误触导致看不清
        }

        private void DynamicIsland_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 鼠标离开不做特殊处理，交由状态机控制
        }

        #endregion

        #region File Station (Black Hole)

        private void DynamicIsland_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                _isFileStationActive = true;
                EnterFileStationMode(dragging: true);
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DynamicIsland_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            e.Handled = true;
        }

        private void DynamicIsland_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            // 如果没有真正存入文件就离开了，恢复原状
            if (_storedFiles.Count == 0 && !IsMouseOver)
            {
                _isFileStationActive = false;
                CheckCurrentSession(); // 恢复媒体或待机
            }
            else if (_storedFiles.Count > 0)
            {
                // 如果有文件，恢复到紧凑的“由文件”状态
                EnterFileStationMode(dragging: false);
            }
        }

        private void DynamicIsland_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    _storedFiles.AddRange(files);
                    UpdateFileStationUI();
                    
                    // 播放吸入动画序列
                    PlaySuckInSequence();
                }
            }
            // _isFileStationActive = (_storedFiles.Count > 0); // Moved to inside sequence
        }

        private void PlaySuckInSequence()
        {
            // 1. 加速旋转并收缩 (吞噬效果)
            var consumeAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) 
            { 
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.5 } 
            };
            
            // 2. 岛屿伴随黑洞震颤
            PlayIslandGlowEffect(Colors.Purple);

            consumeAnim.Completed += (s, ev) =>
            {
                 _isFileStationActive = (_storedFiles.Count > 0);
                 EnterFileStationMode(dragging: false);
            };

            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, consumeAnim);
            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, consumeAnim);
        }

        private void EnterFileStationMode(bool dragging)
        {
            // 隐藏其他面板
            AlbumCover.Visibility = Visibility.Collapsed;
            SongTitle.Visibility = Visibility.Collapsed;
            ControlPanel.Visibility = Visibility.Collapsed;
            VisualizerContainer.Visibility = Visibility.Collapsed;
            NotificationPanel.Visibility = Visibility.Collapsed;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;

            // 显示中转站
            FileStationPanel.Visibility = Visibility.Visible;
            DynamicIsland.Opacity = 1.0;
            SetClickThrough(false);

            if (dragging)
            {
                // 拖拽进入时：黑洞张开
                _widthSpring.Target = 150;
                _heightSpring.Target = 150;
                
                DropHintText.Opacity = 1;
                FileStackDisplay.Visibility = Visibility.Collapsed;
                
                // 黑洞扩张动画
                var scaleAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } };
                BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                
                // 漩涡旋转
                var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever };
                VortexRotation.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
            }
            else
            {
                // 存储状态：紧凑显示
                ShowFileStationState();
            }
        }

        private void ShowFileStationState()
        {
             // 隐藏其他面板确保安全
            AlbumCover.Visibility = Visibility.Collapsed;
            SongTitle.Visibility = Visibility.Collapsed;
            ControlPanel.Visibility = Visibility.Collapsed;
            VisualizerContainer.Visibility = Visibility.Collapsed;
            NotificationPanel.Visibility = Visibility.Collapsed;
            DrinkWaterPanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Collapsed;

            FileStationPanel.Visibility = Visibility.Visible;
            DropHintText.Opacity = 0;
            
            // 黑洞收缩
            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BlackHoleScale.ScaleX = 0; 
            BlackHoleScale.ScaleY = 0;

            // 显示文件堆栈
            FileStackDisplay.Visibility = Visibility.Visible;
            UpdateFileStationUI();

            _widthSpring.Target = 100; // 紧凑宽度
            _heightSpring.Target = 35; // 标准高度
            DynamicIsland.Opacity = 1.0;
            SetClickThrough(false);
        }

        private void UpdateFileStationUI()
        {
             FileCountText.Text = _storedFiles.Count.ToString();
        }

        private void PlayBlackHoleSuckAnimation()
        {
            // 简单的震动反馈或闪光
            PlayIslandGlowEffect(Colors.Purple);
        }

        private void FileStack_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_storedFiles.Count > 0)
            {
                // 开始拖出操作
                var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, _storedFiles.ToArray());
                System.Windows.DragDrop.DoDragDrop(FileStackDisplay, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
                
                // 拖拽完成后清空 (假设用户拖走就是拿走了)
                // 注意：DoDragDrop 是阻塞的，直到拖拽结束
                _storedFiles.Clear();
                _isFileStationActive = false;
                
                // 恢复正常状态
                CheckCurrentSession(); 
            }
        }

        #endregion

    }
}