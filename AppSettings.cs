using System;
using System.IO;
using System.Text.Json;

namespace WinIsland
{
    public class AppSettings
    {
        public bool DrinkWaterEnabled { get; set; } = false;
        public int DrinkWaterIntervalMinutes { get; set; } = 30;
        public bool TodoEnabled { get; set; } = false;
        public string DrinkWaterStartTime { get; set; } = "09:00";
        public string DrinkWaterEndTime { get; set; } = "22:00";
        public DrinkWaterMode DrinkWaterMode { get; set; } = DrinkWaterMode.Interval;
        public List<string> CustomDrinkWaterTimes { get; set; } = new List<string>();
        public List<TodoItem> TodoList { get; set; } = new List<TodoItem>();

        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }

    public class TodoItem
    {
        public DateTime ReminderTime { get; set; }
        public string Content { get; set; }
        public bool IsCompleted { get; set; }
    }
}
