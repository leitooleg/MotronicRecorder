using System;
using System.Reflection;
using System.Windows.Forms;

namespace MotronicRecorder
{
    public static class TimeNavigationTests
    {
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Time navigation: " + message); }
        private static void Near(double actual, double expected, string message) { Assert(Math.Abs(actual - expected) < 0.01, message + ": " + actual + " vs " + expected); }
        private static Sample Add(Recording recording, DateTimeOffset origin, double elapsed, int epoch, string sensor, double value)
        {
            Sample sample = new Sample { Utc = origin.AddMilliseconds(elapsed), ElapsedMs = elapsed, Epoch = epoch, Reply = sensor == "injection" ? (byte)0xEE : (byte)0xF4 };
            sample.Values[sensor] = value; recording.Samples.Add(sample); return sample;
        }
        private static bool Resolve(Recording recording, string text, string date, out double value)
        {
            string error; bool result = TimeNavigation.TryResolve(recording, text, date, out value, out error);
            Assert(result || !string.IsNullOrWhiteSpace(error), "failure must explain why a time cannot be selected");
            return result;
        }

        private static void TimelineCursorTests()
        {
            DateTimeOffset origin = new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
            Recording recording = new Recording { UtcOffsetMinutes = 180 };
            Add(recording, origin, 0, 1, "rpm", 850); Add(recording, origin, 7200000, 1, "rpm", 850);
            using (TimelineView view = new TimelineView())
            {
                view.SetRecording(recording);
                Assert(view.IsFullRange, "initial timeline is full range");
                view.SetWindow(3600000); view.SelectTime(15000);
                Assert(!view.IsFullRange && view.CursorMs.HasValue, "explicit range and persistent cursor");
                MethodInfo keyDown = typeof(TimelineView).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (Keys key in new Keys[] { Keys.End, Keys.Home, Keys.Right, Keys.Left, Keys.Control | Keys.Right, Keys.Shift | Keys.Right })
                {
                    keyDown.Invoke(view, new object[] { new KeyEventArgs(key) });
                    Near(view.SpanMs, 3600000, "cursor key must preserve hour zoom: " + key);
                    Assert(!view.IsFullRange, "cursor key must not reset zoom preset");
                }
                Near(view.CursorMs.Value, 1010, "100 ms / 10 ms / 1 second cursor steps");
                view.ClearSelection(); Assert(!view.CursorMs.HasValue, "clear persistent cursor");
                view.SetWindow(0); Assert(view.IsFullRange, "return to full recording range");
            }
        }

        public static void Run()
        {
            DateTimeOffset origin = new DateTimeOffset(2026, 10, 14, 15, 39, 59, TimeSpan.Zero).AddMilliseconds(500);
            Recording recording = new Recording { UtcOffsetMinutes = 180 };
            Add(recording, origin, 0, 1, "rpm", 850); Add(recording, origin, 1000, 1, "rpm", 400); Add(recording, origin, 3000, 1, "rpm", 850);
            recording.Events.Add(new LogEvent { Utc = origin.AddMilliseconds(250), ElapsedMs = 250, Kind = "manual", Epoch = 1 });
            double elapsed;
            Assert(Resolve(recording, "18:40", "", out elapsed), "minute precision"); Near(elapsed, 500, "HH:mm means exact second zero");
            Assert(TimeNavigation.TimeAt(recording, elapsed).Value.ToString("HH:mm:ss.fff") == "18:40:00.000", "requested whole second retains its displayed precision");
            Assert(Resolve(recording, "18:40:01.123", "14.10.2026", out elapsed), "milliseconds and explicit date"); Near(elapsed, 1623, "millisecond navigation");
            Assert(Resolve(recording, "18:40:00,1", "2026-10-14", out elapsed), "one fractional digit"); Near(elapsed, 600, "fraction padding");
            Assert(!Resolve(recording, "24:00", "", out elapsed), "invalid hour");
            Assert(!Resolve(recording, "18:60", "", out elapsed), "invalid minute");
            Assert(!Resolve(recording, "18:40:61", "", out elapsed), "invalid second");
            Assert(!Resolve(recording, "18:40.12", "", out elapsed), "fraction needs seconds");
            Assert(!Resolve(recording, "18:40", "14/10/2026", out elapsed), "date format is explicit");
            Assert(!Resolve(recording, "18:39", "", out elapsed), "outside recording");
            Assert(TimeNavigation.DisplayTime(recording, origin).Hour == 18, "recorded UTC offset, independent of this PC");
            Recording submillisecond = new Recording { UtcOffsetMinutes = 180 };
            Sample clockLeft = Add(submillisecond, origin, 31135.5543, 1, "rpm", 850);
            clockLeft.Utc = new DateTimeOffset(2026, 10, 14, 15, 39, 59, TimeSpan.Zero).AddTicks(8907365);
            Sample clockRight = Add(submillisecond, origin, 31407.7612, 1, "rpm", 850);
            clockRight.Utc = clockLeft.Utc.AddTicks(2721234);
            Assert(Resolve(submillisecond, "18:40:00", "", out elapsed), "submillisecond source timestamps resolve");
            Assert(TimeNavigation.TimeAt(submillisecond, elapsed).Value.ToString("HH:mm:ss.fff") == "18:40:00.000", "submillisecond anchors do not display the previous second after exact navigation");
            Assert(TimeNavigation.TimeAt(recording, 500).Value.Hour == 18 && TimeNavigation.TimeAt(recording, 500).Value.Offset == TimeSpan.FromHours(3), "TimeAt returns recording display time, not raw UTC");
            Recording legacy = new Recording();
            Assert(TimeNavigation.DisplayTime(legacy, origin).EqualsExact(origin.ToLocalTime()), "old recording falls back to local zone");

            Recording night = new Recording { UtcOffsetMinutes = 180 };
            DateTimeOffset midnightOrigin = new DateTimeOffset(2026, 10, 14, 20, 59, 59, TimeSpan.Zero);
            Add(night, midnightOrigin, 0, 1, "rpm", 850); Add(night, midnightOrigin, 2000, 1, "rpm", 850);
            Assert(Resolve(night, "00:00", "", out elapsed), "midnight without date when unique"); Near(elapsed, 1000, "midnight position");
            Assert(Resolve(night, "00:00", "2026-10-15", out elapsed), "midnight with next date");
            Assert(!Resolve(night, "00:00", "2026-10-14", out elapsed), "wrong midnight date");

            Recording days = new Recording { UtcOffsetMinutes = 180 };
            Add(days, origin, 500, 1, "rpm", 850); Add(days, origin, 86400500, 1, "rpm", 850);
            Assert(!Resolve(days, "18:40", "", out elapsed), "same wall time on two dates is ambiguous");
            Assert(Resolve(days, "18:40", "2026-10-15", out elapsed), "date disambiguates days"); Near(elapsed, 86400500, "next day position");

            Recording clockBack = new Recording { UtcOffsetMinutes = 0 };
            DateTimeOffset wall = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
            Add(clockBack, wall, 0, 1, "rpm", 850); Add(clockBack, wall, 1000, 1, "rpm", 850);
            Sample changed = Add(clockBack, wall, 2000, 1, "rpm", 850); changed.Utc = wall;
            changed = Add(clockBack, wall, 3000, 1, "rpm", 850); changed.Utc = wall.AddSeconds(1);
            Assert(!Resolve(clockBack, "18:00:00.500", "2026-10-14", out elapsed), "clock rollback is ambiguous even with date");
            Recording jump = new Recording { UtcOffsetMinutes = 0 };
            Add(jump, wall, 0, 1, "rpm", 850); changed = Add(jump, wall, 1000, 1, "rpm", 850); changed.Utc = wall.AddHours(1).AddSeconds(1);
            Assert(!Resolve(jump, "18:30", "", out elapsed), "forward clock jump must not invent timestamps");
            Assert(!TimeNavigation.TimeAt(jump, 500).HasValue, "axis labels do not interpolate a clock jump");
            Assert(TimeNavigation.TimeAt(jump, 1000).Value == changed.Utc, "actual sample timestamps are authoritative");

            Recording points = new Recording { UtcOffsetMinutes = 0 };
            Sample first = Add(points, wall, 0, 1, "rpm", 850);
            Sample near = Add(points, wall, 200, 1, "rpm", 900);
            Add(points, wall, 210, 1, "injection", 2.5); Add(points, wall, 400, 1, "rpm", 400);
            NearestReading reading = TimeNavigation.FindNearest(points, "rpm", 250, 100);
            Assert(reading != null && object.ReferenceEquals(reading.Sample, near), "nearest returns original sample, not a synthetic row"); Near(reading.Value, 900, "actual value"); Near(reading.OffsetMs, -50, "signed offset");
            reading = TimeNavigation.FindNearest(points, "injection", 250, 100);
            Assert(reading != null && reading.Sample.Reply == 0xEE, "secondary reply retains its real timestamp"); Near(reading.OffsetMs, -40, "secondary timestamp offset");
            Assert(!first.Values.ContainsKey("injection"), "no values carried into F4");
            Assert(TimeNavigation.FindNearest(points, "rpm", 350, 20) == null, "outside tolerance");
            Assert(TimeNavigation.FindNearest(points, "missing", 200) == null, "absent sensor");
            Assert(TimeNavigation.FindNearest(points, "rpm", -1) == null, "before recording");
            Assert(TimeNavigation.FindNearest(points, "rpm", double.NaN) == null, "invalid cursor");

            Recording gap = new Recording();
            Add(gap, wall, 0, 1, "rpm", 850); Add(gap, wall, 2000, 1, "rpm", 400);
            Assert(TimeNavigation.FindNearest(gap, "rpm", 100) == null, "inside a missing period cannot borrow even the nearby edge");
            Assert(TimeNavigation.FindNearest(gap, "rpm", 2000) != null, "actual edge sample is available");
            Recording epochs = new Recording();
            Add(epochs, wall, 0, 1, "injection", 2.5); Add(epochs, wall, 500, 2, "rpm", 850); Add(epochs, wall, 700, 2, "rpm", 850);
            Assert(TimeNavigation.FindNearest(epochs, "rpm", 250) == null, "cursor between epochs is a gap");
            Assert(TimeNavigation.FindNearest(epochs, "injection", 550) == null, "never borrow a sensor from the previous epoch");
            Recording loss = new Recording();
            Add(loss, wall, 0, 1, "rpm", 850); Add(loss, wall, 200, 1, "rpm", 850); Add(loss, wall, 400, 1, "rpm", 400); Add(loss, wall, 600, 1, "rpm", 850);
            loss.Events.Add(new LogEvent { Utc = wall.AddMilliseconds(250), ElapsedMs = 250, Kind = "link_lost", Epoch = 1 });
            loss.Events.Add(new LogEvent { Utc = wall.AddMilliseconds(550), ElapsedMs = 550, Kind = "link_restored", Epoch = 1 });
            Assert(TimeNavigation.FindNearest(loss, "rpm", 300) == null, "explicit link loss blocks values even if epoch was not changed");
            reading = TimeNavigation.FindNearest(loss, "rpm", 560);
            Assert(reading != null && reading.Sample.ElapsedMs == 600, "only the restored side may supply a value");

            Recording eventOnly = new Recording { UtcOffsetMinutes = 0 };
            Recording switchedConfiguration = new Recording { UtcOffsetMinutes = 0 };
            Sample lambdaFrame = Add(switchedConfiguration, wall, 0, 1, "lambda_mv", 730); lambdaFrame.Values["lambda_enabled"] = 1;
            Sample coFrame = Add(switchedConfiguration, wall, 200, 1, "co_potentiometer", 1.25); coFrame.Values["lambda_enabled"] = 0;
            Sample coNext = Add(switchedConfiguration, wall, 400, 1, "co_potentiometer", 1.3); coNext.Values["lambda_enabled"] = 0;
            Assert(TimeNavigation.FindNearest(switchedConfiguration, "lambda_mv", 0) != null, "configured lambda keeps its actual reading");
            Assert(TimeNavigation.FindNearest(switchedConfiguration, "lambda_mv", 200) == null, "cursor does not borrow lambda from another configuration");
            Assert(TimeNavigation.FindNearest(switchedConfiguration, "co_potentiometer", 0) == null, "cursor does not borrow CO from another configuration");
            Assert(TimeNavigation.FindNearest(switchedConfiguration, "co_potentiometer", 200) != null, "CO reading is available in its own configuration");
            eventOnly.Events.Add(new LogEvent { Utc = wall, ElapsedMs = 0 }); eventOnly.Events.Add(new LogEvent { Utc = wall.AddSeconds(1), ElapsedMs = 1000 });
            Assert(Resolve(eventOnly, "18:00:00.500", "", out elapsed), "events also anchor real recording time"); Near(elapsed, 500, "event-only time position");
            Assert(TimeNavigation.FindNearest(eventOnly, "rpm", 500) == null, "events do not invent sensor values");
            TimelineCursorTests();
            Console.WriteLine("Time navigation tests: passed (precise time, saved offset, midnight/date ambiguity, clock jumps, actual F4/EE values, tolerance, epoch and link-loss gaps). No hardware accessed.");
        }
    }
}
