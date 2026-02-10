using CsvHelper;
using CsvHelper.Configuration.Attributes;
using System.Diagnostics;
using System.Globalization;

namespace TimeViewer
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        private void OnCounterClicked(object? sender, EventArgs e)
        {
            GetManicTimeExport();
        }

        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        // Reading Tables (Tags & Time)
        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        public class TagTable
        {
            public string Process { get; set; }
            public string Tag { get; set; }
        }

        private static readonly string TagsPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");
        public static async Task<List<TagTable>> GetTagTable()
        {
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            if (!File.Exists(TagsPath))
            {
                await File.WriteAllTextAsync(TagsPath, "Process,Tag" + Environment.NewLine);
            }
            Debug.WriteLine($"Tags file path: {TagsPath}");

            List<TagTable> tags;

            using (var reader = new StreamReader(TagsPath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                tags = csv.GetRecords<TagTable>().ToList();
            }
            return tags;
        }

        public class TimeTable
        {
            public string Name { get; set; }
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public string Duration { get; set; } // maybe TimeOnly
            public string Process { get; set; }

            [Ignore]
            public string Tag { get; set; }
        }
        private static async Task<List<TimeTable>> GetTimeTable()
        {
            // Running mtc to export CSV to tempCsvPath
            Process process = new();
            string tempCsvPath = Path.Combine(FileSystem.CacheDirectory, "manictime-export.csv");

            ProcessStartInfo startInfo = new()
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = @"C:\Program Files\ManicTime\mtc.exe",
                Arguments = $"export ManicTime/Applications \"{tempCsvPath}\"",
                CreateNoWindow = true
            };
            process.StartInfo = startInfo;
            process.Start();

            // Reading Exported Applications Table
            List<TimeTable> applications;

            using (var reader = new StreamReader(tempCsvPath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                //applications = csv.GetRecords<TimeTable>().Select(AddTag).ToList();
                applications = csv.GetRecords<TimeTable>().ToList();
            }

            return applications;
        }

        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        // Reading Tables (Tags & Time)
        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        private static async Task GetManicTimeExport()
        {
            List<TagTable> tags = await GetTagTable();
            List<TimeTable> apps = await GetTimeTable();

            // Query Syntax
            var merged = from app in apps
                        join tag in tags on app.Process equals tag.Process into gj
                        from subgroup in gj.DefaultIfEmpty()
                         select new TimeTable
                        {
                            Name = app.Name,
                            Start = app.Start,
                            End = app.End,
                            Duration = app.Duration,
                            Process = app.Process,
                            Tag = subgroup?.Tag ?? "No Clue"
                        };

            // Tests
            var today = merged.Where(a => a.Start.Date == DateTime.Today).ToList();
            var yesterday = merged.Where(a => a.Start.Date == DateTime.Today.Subtract(TimeSpan.FromDays(1))).ToList();

            foreach (var row in yesterday)
            {
                Debug.WriteLine($"{row.Tag} - {row.Name} - {row.Start} - {row.End} - {row.Duration} - {row.Process}");
            }
        }
    }
}
