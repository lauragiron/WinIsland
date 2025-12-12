using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinIsland
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void LoadSettings()
        {
            var settings = AppSettings.Load();
            
            // 系统设置
            ChkStartWithWindows.IsChecked = IsStartupEnabled();

            // 喝水提醒
            ChkDrinkWater.IsChecked = settings.DrinkWaterEnabled;
            TxtDrinkWaterInterval.Text = settings.DrinkWaterIntervalMinutes.ToString();
            TxtDrinkStartTime.Text = settings.DrinkWaterStartTime;
            TxtDrinkEndTime.Text = settings.DrinkWaterEndTime;

            if (settings.DrinkWaterMode == DrinkWaterMode.Custom)
                RbModeCustom.IsChecked = true;
            else
                RbModeInterval.IsChecked = true;

            ListCustomDrinkTimes.ItemsSource = settings.CustomDrinkWaterTimes;
            
            // 待办事项
            ChkTodo.IsChecked = settings.TodoEnabled;
            ListTodo.ItemsSource = settings.TodoList;
            DpTodoDate.SelectedDate = DateTime.Today;
            
            UpdateDrinkWaterUI();
            UpdateTodoUI();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // 系统自启动
            if (ChkStartWithWindows.IsChecked == true) EnableStartup();
            else DisableStartup();

            var settings = AppSettings.Load();
            
            // 喝水提醒
            settings.DrinkWaterEnabled = ChkDrinkWater.IsChecked == true;
            if (int.TryParse(TxtDrinkWaterInterval.Text, out int interval))
            {
                settings.DrinkWaterIntervalMinutes = Math.Max(1, interval);
            }
            settings.DrinkWaterStartTime = TxtDrinkStartTime.Text;
            settings.DrinkWaterEndTime = TxtDrinkEndTime.Text;
            settings.DrinkWaterMode = RbModeCustom.IsChecked == true ? DrinkWaterMode.Custom : DrinkWaterMode.Interval;
            
            // 待办事项
            settings.TodoEnabled = ChkTodo.IsChecked == true;

            settings.Save();

            // 通知主窗口重新加载设置
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.ReloadSettings();
            }

            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("WinIsland") != null;
            }
            catch { return false; }
        }

        private void EnableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("WinIsland", System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法启用开机自启: {ex.Message}");
            }
        }

        private void DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("WinIsland", false);
            }
            catch { }
        }

        private void ChkDrinkWater_Checked(object sender, RoutedEventArgs e) => UpdateDrinkWaterUI();
        private void ChkDrinkWater_Unchecked(object sender, RoutedEventArgs e) => UpdateDrinkWaterUI();
        private void RbMode_Checked(object sender, RoutedEventArgs e) => UpdateDrinkWaterUI();

        private void UpdateDrinkWaterUI()
        {
            if (PanelDrinkWaterSettings == null) return;

            if (ChkDrinkWater.IsChecked == true)
            {
                PanelDrinkWaterSettings.Visibility = Visibility.Visible;
                if (RbModeCustom.IsChecked == true)
                {
                    PanelModeInterval.Visibility = Visibility.Collapsed;
                    PanelModeCustom.Visibility = Visibility.Visible;
                }
                else
                {
                    PanelModeInterval.Visibility = Visibility.Visible;
                    PanelModeCustom.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PanelDrinkWaterSettings.Visibility = Visibility.Collapsed;
            }
        }

        private void ChkTodo_Checked(object sender, RoutedEventArgs e) => UpdateTodoUI();
        private void ChkTodo_Unchecked(object sender, RoutedEventArgs e) => UpdateTodoUI();

        private void UpdateTodoUI()
        {
            if (PanelTodo == null) return;
            PanelTodo.Visibility = ChkTodo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- 自定义时间逻辑 ---

        private void BtnAddCustomDrinkTime_Click(object sender, RoutedEventArgs e)
        {
            string input = TxtCustomDrinkTime.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            if (!TryParseTime(input, out string formattedTime))
            {
                MessageBox.Show("时间格式错误，请使用 HH:MM");
                return;
            }

            var settings = AppSettings.Load(); 

            // 重新加载以进行修改
            var currentList = (List<string>)ListCustomDrinkTimes.ItemsSource ?? new List<string>();
            if (!currentList.Contains(formattedTime))
            {
                currentList.Add(formattedTime);
                currentList.Sort();
                
                // 刷新列表
                ListCustomDrinkTimes.ItemsSource = null;
                ListCustomDrinkTimes.ItemsSource = currentList;
                
                // 立即保存
                var s = AppSettings.Load();
                s.CustomDrinkWaterTimes = currentList;
                s.Save();
            }
            
            TxtCustomDrinkTime.Text = "";
        }

        private void BtnDeleteCustomDrinkTime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string timeStr)
            {
                var currentList = (List<string>)ListCustomDrinkTimes.ItemsSource;
                if (currentList != null && currentList.Remove(timeStr))
                {
                    ListCustomDrinkTimes.ItemsSource = null;
                    ListCustomDrinkTimes.ItemsSource = currentList;

                    var s = AppSettings.Load();
                    s.CustomDrinkWaterTimes = currentList;
                    s.Save();
                }
            }
        }

        // --- 待办事项逻辑 ---

        private void BtnAddTodo_Click(object sender, RoutedEventArgs e)
        {
            if (DpTodoDate.SelectedDate == null) return;
            if (!TimeSpan.TryParse(TxtTodoTime.Text, out TimeSpan time)) return;
            if (string.IsNullOrWhiteSpace(TxtTodoContent.Text)) return;

            var newItem = new TodoItem
            {
                ReminderTime = DpTodoDate.SelectedDate.Value.Date + time,
                Content = TxtTodoContent.Text,
                IsCompleted = false
            };

            var currentList = (List<TodoItem>)ListTodo.ItemsSource ?? new List<TodoItem>();
            currentList.Add(newItem);
            
            // 按时间排序
            currentList.Sort((a, b) => a.ReminderTime.CompareTo(b.ReminderTime));

            ListTodo.ItemsSource = null;
            ListTodo.ItemsSource = currentList;

            // 立即保存
            var s = AppSettings.Load();
            s.TodoList = currentList;
            s.Save();

            TxtTodoContent.Text = "";
        }

        private void BtnDeleteTodo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TodoItem item)
            {
                var currentList = (List<TodoItem>)ListTodo.ItemsSource;
                if (currentList != null)
                {
                    // 移除匹配的项目
                    currentList.RemoveAll(x => x.Content == item.Content && x.ReminderTime == item.ReminderTime);
                    
                    ListTodo.ItemsSource = null;
                    ListTodo.ItemsSource = currentList;

                    var s = AppSettings.Load();
                    s.TodoList = currentList;
                    s.Save();
                }
            }
        }

        // --- 辅助方法 ---

        private bool TryParseTime(string input, out string formattedTime)
        {
            formattedTime = "";
            input = input.Replace(" ", "").Replace(":", "");
            
            // 支持 930 -> 09:30, 1400 -> 14:00
            if (input.Length == 3) input = "0" + input;
            
            if (input.Length == 4 && int.TryParse(input, out _))
            {
                string h = input.Substring(0, 2);
                string m = input.Substring(2, 2);
                if (int.Parse(h) < 24 && int.Parse(m) < 60)
                {
                    formattedTime = $"{h}:{m}";
                    return true;
                }
            }
            
            // 标准解析
            if (TimeSpan.TryParse(input, out TimeSpan ts))
            {
                formattedTime = ts.ToString(@"hh\:mm");
                return true;
            }

            return false;
        }
    }
}
