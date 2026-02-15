using CsvHelper;
using CsvHelper.Configuration.Attributes;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TimeViewer
{
    public partial class MainPage : ContentPage, INotifyPropertyChanged
    {
        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadDayPieAsync(DateTime.Today);
        }

        private async void OnRefreshClicked(object? sender, EventArgs e)
        {
            await LoadDayPieAsync(DateTime.Today);
        }


        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        // Parameters
        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        int MaxRadialColumnWidth = 100;

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

            using var reader = new StreamReader(TagsPath);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            return csv.GetRecords<TagTable>().ToList();
        }

        public class TimeTable
        {
            public string Name { get; set; }
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public string Duration { get; set; }
            public string Process { get; set; }

            [Ignore]
            public string Tag { get; set; }

            public static string ToString(TimeTable x)
            {
                string Message = $"{x.Name} | {x.Start} | {x.End} | {x.Duration} | {x.Process}";
                if (x.Tag is not null)
                {
                    Message += $" | {x.Tag}";
                }
                return Message;
            }
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
            using var reader = new StreamReader(tempCsvPath);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            return csv.GetRecords<TimeTable>().ToList();
        }

        private static List<TimeTable> MergeAppTags(List<TimeTable> apps, List<TagTable> tags)
        {
            // Merging Tags into Applications by Process Name
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

            return merged.ToList();
        }

        // Util
        public static void PrintTable(List<TimeTable> table)
        {
            foreach (var row in table)
            {
                Debug.WriteLine(row.ToString());
            }
        }

        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        // Graphs
        // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

        private ObservableCollection<ISeries> _daySeries = new();
        public ObservableCollection<ISeries> DaySeries
        {
            get => _daySeries;
            set
            {
                _daySeries = value;
                OnPropertyChanged(nameof(DaySeries));
            }
        }

        public class LegendItem
        {
            public string Name { get; set; }
            public string Duration { get; set; }
        }

        private ObservableCollection<LegendItem> _legendItems = new();
        public ObservableCollection<LegendItem> LegendItems
        {
            get => _legendItems;
            set
            {
                _legendItems = value;
                OnPropertyChanged(nameof(LegendItems));
            }
        }

        private async Task LoadDayPieAsync(DateTime day)
        {
            List<TagTable> tags = await GetTagTable();
            List<TimeTable> apps = await GetTimeTable();
            apps = MergeAppTags(apps, tags);

            double totalSeconds = 0;
            var grouped = apps
                .Where(a => a.Start.Date == day) // Data only of day
                .GroupBy(a => a.Process)    // Grouped by the Process
                .Select(a => new    // Parsing and Summing the Duration for each Process
                {
                    Process = a.Key,
                    Seconds = a.Sum(b => TimeSpan.Parse(b.Duration).TotalSeconds)
                })
                .Where(a => a.Seconds > 5)  // Remove anything less than 5 seconds
                .OrderBy(a => a.Seconds) // Order by Duration
                .Select(a =>    // Format into Series for PieChart and Legend
                {
                    totalSeconds += a.Seconds;
                    return (Series: new PieSeries<double>
                    {
                        Name = a.Process,
                        Values = new[] { a.Seconds },
                        ToolTipLabelFormatter = (p) => TimeSpan.FromSeconds(p.Model).ToString(@"hh\:mm"),
                        MaxRadialColumnWidth = MaxRadialColumnWidth
                    },
                    Legend: new LegendItem
                    {
                        Name = a.Process,
                        Duration = TimeSpan.FromSeconds(a.Seconds).ToString(@"hh\:mm")
                    });
                })
                .ToList();

            // add remainder to reach 24h
            var remaining = Math.Max(0, TimeSpan.FromHours(24).TotalSeconds - totalSeconds);
            grouped.Add((Series: new PieSeries<double>
            {
                Name="Remaining",
                Values = new[] {remaining},
                ToolTipLabelFormatter = p => TimeSpan.FromSeconds(p.Model).ToString(@"hh\:mm"),
                MaxRadialColumnWidth = MaxRadialColumnWidth
            }, Legend: new LegendItem
            {
                Name = "Remaining",
                Duration = TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm")
            }));

            // Bind to UI
            DaySeries.Clear();
            LegendItems.Clear();
            foreach (var items in grouped)
            {
                DaySeries.Add(items.Series);
                LegendItems.Add(items.Legend);
            }
        }
    }
}
