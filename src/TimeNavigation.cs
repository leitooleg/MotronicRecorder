using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MotronicRecorder
{
    public sealed class NearestReading
    {
        public Sample Sample { get; internal set; }
        public double Value { get; internal set; }
        public double OffsetMs { get; internal set; }
    }

    public static class TimeNavigation
    {
        private const double Epsilon = 0.0001;
        private sealed class Anchor
        {
            public DateTimeOffset Utc;
            public double Elapsed;
            public Anchor(DateTimeOffset utc, double elapsed) { Utc = utc; Elapsed = elapsed; }
        }

        public static DateTimeOffset DisplayTime(Recording recording, DateTimeOffset utc)
        {
            if (recording != null && recording.UtcOffsetMinutes.HasValue)
            {
                int minutes = recording.UtcOffsetMinutes.Value;
                if (minutes >= -840 && minutes <= 840) return utc.ToOffset(TimeSpan.FromMinutes(minutes));
            }
            return utc.ToLocalTime();
        }

        // Returns display time in the recording's saved offset (or this PC's local zone).
        // Labels use actual captured timestamps, not one assumed recording origin.
        public static DateTimeOffset? TimeAt(Recording recording, double elapsedMs)
        {
            if (recording == null || !Finite(elapsedMs)) return null;
            Anchor before = null, after = null;
            int at = LowerBound(recording.Samples, elapsedMs);
            if (at < recording.Samples.Count) Consider(recording.Samples[at].Utc, recording.Samples[at].ElapsedMs, elapsedMs, ref before, ref after);
            if (at > 0) Consider(recording.Samples[at - 1].Utc, recording.Samples[at - 1].ElapsedMs, elapsedMs, ref before, ref after);
            foreach (LogEvent item in recording.Events) Consider(item.Utc, item.ElapsedMs, elapsedMs, ref before, ref after);
            if (before != null && Math.Abs(before.Elapsed - elapsedMs) < Epsilon) return DisplayTime(recording, before.Utc);
            if (after != null && Math.Abs(after.Elapsed - elapsedMs) < Epsilon) return DisplayTime(recording, after.Utc);
            if (before == null || after == null || !ClockContinuous(before, after)) return null;
            double ratio = (elapsedMs - before.Elapsed) / (after.Elapsed - before.Elapsed);
            // Round to the nearest tick: truncation can put an exact clock jump
            // one tick before the requested second (18:40:00 -> 18:39:59.999).
            return DisplayTime(recording, before.Utc.AddTicks((long)Math.Round((after.Utc - before.Utc).Ticks * ratio)));
        }

        private static void Consider(DateTimeOffset utc, double elapsed, double target, ref Anchor before, ref Anchor after)
        {
            if (!Finite(elapsed)) return;
            if (elapsed <= target && (before == null || elapsed > before.Elapsed)) before = new Anchor(utc, elapsed);
            if (elapsed >= target && (after == null || elapsed < after.Elapsed)) after = new Anchor(utc, elapsed);
        }

        public static bool TryResolve(Recording recording, string clockText, string dateText, out double elapsedMs, out string error)
        {
            elapsedMs = 0; error = null;
            Match match = Regex.Match((clockText ?? "").Trim(), @"^(\d{1,2}):(\d{2})(?::(\d{2})(?:[.,](\d{1,3}))?)?$");
            int hour, minute, second = 0, millisecond = 0;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out hour) || !int.TryParse(match.Groups[2].Value, out minute))
            { error = "Введите время ЧЧ:мм, ЧЧ:мм:сс или ЧЧ:мм:сс.fff (например 18:40:12.350)."; return false; }
            if (match.Groups[3].Success) second = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (match.Groups[4].Success) millisecond = int.Parse(match.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            if (hour > 23 || minute > 59 || second > 59)
            { error = "Время вне допустимого диапазона: часы 00–23, минуты и секунды 00–59."; return false; }
            DateTime selectedDate;
            bool hasDate = !string.IsNullOrWhiteSpace(dateText);
            if (hasDate && !DateTime.TryParseExact(dateText.Trim(), new string[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out selectedDate))
            { error = "Дата должна быть ГГГГ-ММ-ДД или ДД.ММ.ГГГГ."; return false; }
            List<Anchor> anchors = Anchors(recording);
            if (anchors.Count == 0) { error = "В записи пока нет отметок времени."; return false; }
            HashSet<DateTime> dates = new HashSet<DateTime>();
            if (hasDate)
            {
                DateTime.TryParseExact(dateText.Trim(), new string[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out selectedDate);
                dates.Add(selectedDate.Date);
            }
            else
            {
                foreach (Anchor anchor in anchors) dates.Add(DisplayTime(recording, anchor.Utc).Date);
                // Include dates inside long but continuous gaps as well as midnight endpoints.
                for (int i = 1; i < anchors.Count; i++)
                {
                    if (!ClockContinuous(anchors[i - 1], anchors[i])) continue;
                    DateTime left = DisplayTime(recording, anchors[i - 1].Utc).Date;
                    DateTime right = DisplayTime(recording, anchors[i].Utc).Date;
                    if ((right - left).TotalDays > 31) { error = "Для такой длинной записи укажите дату перехода."; return false; }
                    if (left < right) for (DateTime date = left.AddDays(1); date < right; date = date.AddDays(1)) dates.Add(date);
                }
            }
            List<double> candidates = new List<double>();
            foreach (DateTime date in dates)
            {
                DateTime target = date.AddHours(hour).AddMinutes(minute).AddSeconds(second).AddMilliseconds(millisecond);
                for (int i = 0; i < anchors.Count; i++)
                {
                    Anchor current = anchors[i]; DateTime currentWall = DisplayTime(recording, current.Utc).DateTime;
                    if (Math.Abs((target - currentWall).TotalMilliseconds) < Epsilon) AddCandidate(candidates, current.Elapsed);
                    if (i == 0) continue;
                    Anchor previous = anchors[i - 1];
                    if (!ClockContinuous(previous, current)) continue;
                    DateTime previousWall = DisplayTime(recording, previous.Utc).DateTime;
                    double wallSpan = (currentWall - previousWall).TotalMilliseconds;
                    double utcSpan = (current.Utc - previous.Utc).TotalMilliseconds;
                    if (wallSpan <= 0 || Math.Abs(wallSpan - utcSpan) > 1) continue; // DST or changed recorded offset.
                    if (target < previousWall || target > currentWall) continue;
                    double ratio = (target - previousWall).TotalMilliseconds / wallSpan;
                    AddCandidate(candidates, previous.Elapsed + (current.Elapsed - previous.Elapsed) * ratio);
                }
            }
            if (candidates.Count == 0)
            {
                error = "Это время вне записи или попало в скачок системных часов. Укажите дату при переходе через полночь и проверьте диапазон.";
                return false;
            }
            if (candidates.Count > 1)
            {
                error = hasDate ? "Это время соответствует нескольким участкам записи (перевод часов). Выберите участок по событию или на графике." : "Это время встречается в записи несколько раз. Укажите дату; при переводе часов выберите участок на графике.";
                return false;
            }
            elapsedMs = candidates[0]; return true;
        }

        private static void AddCandidate(List<double> values, double value)
        {
            foreach (double existing in values) if (Math.Abs(existing - value) < 1) return;
            values.Add(value);
        }

        private static List<Anchor> Anchors(Recording recording)
        {
            List<Anchor> result = new List<Anchor>();
            if (recording == null) return result;
            foreach (Sample sample in recording.Samples) if (Finite(sample.ElapsedMs)) result.Add(new Anchor(sample.Utc, sample.ElapsedMs));
            foreach (LogEvent item in recording.Events) if (Finite(item.ElapsedMs)) result.Add(new Anchor(item.Utc, item.ElapsedMs));
            result.Sort(delegate(Anchor a, Anchor b) { return a.Elapsed.CompareTo(b.Elapsed); });
            return result;
        }

        private static bool ClockContinuous(Anchor left, Anchor right)
        {
            double elapsed = right.Elapsed - left.Elapsed;
            return elapsed > Epsilon && Math.Abs((right.Utc - left.Utc).TotalMilliseconds - elapsed) <= 50;
        }

        public static NearestReading FindNearest(Recording recording, string sensor, double elapsedMs, double toleranceMs = 1000)
        {
            if (recording == null || recording.Samples.Count == 0 || string.IsNullOrEmpty(sensor) || !Finite(elapsedMs) || !Finite(toleranceMs) || toleranceMs < 0) return null;
            // CO and lambda are alternative meanings of one byte. Do not borrow a
            // measurement from a neighboring F4 with a different configuration.
            if (sensor == "lambda_mv" || sensor == "co_potentiometer")
            {
                NearestReading configuration = FindNearest(recording, "lambda_enabled", elapsedMs, toleranceMs);
                if (configuration == null || configuration.Value != (sensor == "lambda_mv" ? 1 : 0)) return null;
            }
            IList<Sample> samples = recording.Samples;
            int at = LowerBound(samples, elapsedMs);
            if (elapsedMs < samples[0].ElapsedMs - Epsilon) return null;
            double end = samples[samples.Count - 1].ElapsedMs;
            foreach (LogEvent item in recording.Events) end = Math.Max(end, item.ElapsedMs);
            if (elapsedMs > end + Epsilon) return null;
            bool exact = at < samples.Count && Math.Abs(samples[at].ElapsedMs - elapsedMs) < Epsilon;
            int epoch;
            if (exact) epoch = samples[at].Epoch;
            else if (at > 0 && at < samples.Count)
            {
                Sample left = samples[at - 1], right = samples[at];
                if (left.Epoch != right.Epoch || right.ElapsedMs - left.ElapsedMs > 1000) return null;
                epoch = left.Epoch;
            }
            else epoch = samples[Math.Max(0, at - 1)].Epoch;
            if (!exact && IsLostAt(recording, elapsedMs)) return null;
            NearestReading nearest = null;
            int from = LowerBound(samples, elapsedMs - toleranceMs);
            int to = LowerBound(samples, elapsedMs + toleranceMs + Epsilon);
            for (int i = from; i < to; i++)
            {
                Sample sample = samples[i]; double value;
                if (sample.Epoch != epoch || !sample.Values.TryGetValue(sensor, out value) || !Finite(value)) continue;
                if (HasBoundaryBetween(recording, elapsedMs, sample.ElapsedMs)) continue;
                double offset = sample.ElapsedMs - elapsedMs;
                if (nearest == null || Math.Abs(offset) < Math.Abs(nearest.OffsetMs)) nearest = new NearestReading { Sample = sample, Value = value, OffsetMs = offset };
            }
            return nearest;
        }

        private static bool IsLostAt(Recording recording, double elapsed)
        {
            LogEvent last = null;
            foreach (LogEvent item in recording.Events)
                if (item.ElapsedMs <= elapsed && (item.Kind == "link_lost" || item.Kind == "connect_attempt" || item.Kind == "link_restored") && (last == null || item.ElapsedMs >= last.ElapsedMs)) last = item;
            return last != null && last.Kind != "link_restored";
        }

        internal static bool HasBoundaryBetween(Recording recording, double left, double right)
        {
            if (left > right) { double swap = left; left = right; right = swap; }
            if (Math.Abs(left - right) < Epsilon) return false;
            foreach (LogEvent item in recording.Events)
                if (item.ElapsedMs > left + Epsilon && item.ElapsedMs <= right + Epsilon && (item.Kind == "link_lost" || item.Kind == "connect_attempt" || item.Kind == "link_restored")) return true;
            return false;
        }

        private static int LowerBound(IList<Sample> samples, double elapsed)
        {
            int lo = 0, hi = samples.Count;
            while (lo < hi) { int middle = lo + (hi - lo) / 2; if (samples[middle].ElapsedMs < elapsed) lo = middle + 1; else hi = middle; }
            return lo;
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
