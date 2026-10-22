using System;
using System.Collections.Generic;

namespace Doorman.Core
{
    /// <summary>
    /// Keeps the log honest about one of the steps that run every frame (the settings check, the
    /// player count, the invite check, the live feed), without filling it.
    ///
    /// Each step is tried on its own, so one that keeps failing never stops the others. This decides
    /// which failures get a log line: the first one at once; a kind of failure not yet logged since the
    /// step last worked, at most one line every <see cref="NewKindGapSeconds"/>; a kind already logged,
    /// again only every <see cref="RepeatSeconds"/>, with a count of the failures in between; and one
    /// line when the step works again. It returns the line to write, or null for none, and writes
    /// nothing itself.
    /// </summary>
    public sealed class StepHealth
    {
        /// <summary>A failure already logged is said again, with a count, at most this often.</summary>
        public const double RepeatSeconds = 300d;

        /// <summary>A step gets at most one line this close together, even when the failure changes.</summary>
        public const double NewKindGapSeconds = 10d;

        /// <summary>
        /// Kinds of failure remembered per failing stretch. Past this every failure counts as one
        /// already logged, so a message that changes each time (an index, a name) cannot fill the log.
        /// </summary>
        public const int MaxKinds = 16;

        /// <summary>The longest exception text a line carries.</summary>
        public const int MaxDetail = 300;

        private readonly string _name;
        private readonly Func<double> _now;
        private readonly HashSet<string> _kindsSaid = new HashSet<string>(StringComparer.Ordinal);
        private double _lastLineAt = double.NegativeInfinity;
        private double _lastOkAt = double.NegativeInfinity;
        private int _unsaid;

        /// <param name="name">How the log names the step, for example "invite check".</param>
        /// <param name="nowSeconds">A steady clock in seconds (a stopwatch, not the wall clock).</param>
        public StepHealth(string name, Func<double> nowSeconds)
        {
            _name = string.IsNullOrEmpty(name) ? "a step" : name;
            _now = nowSeconds ?? throw new ArgumentNullException(nameof(nowSeconds));
        }

        public string Name => _name;

        /// <summary>True from a failure until the step next works.</summary>
        public bool Failing { get; private set; }

        /// <summary>Failures since the step last worked.</summary>
        public int FailuresInARow { get; private set; }

        /// <summary>True when the step is not failing and has worked within the last <paramref name="seconds"/>.</summary>
        public bool WorkedWithin(double seconds)
        {
            return !Failing && _now() - _lastOkAt <= seconds;
        }

        /// <summary>The step ran to the end. Returns a "working again" line after a failure that was logged.</summary>
        public string Succeeded()
        {
            _lastOkAt = _now();
            if (!Failing)
            {
                return null;
            }

            int failures = FailuresInARow;
            bool said = _kindsSaid.Count > 0;
            Failing = false;
            FailuresInARow = 0;
            _kindsSaid.Clear();
            _unsaid = 0;
            return said ? Capital(_name) + " is working again after " + Count(failures) + "." : null;
        }

        /// <summary>The step threw. Returns the line to log, or null when this one is only counted.</summary>
        public string Failed(Exception ex)
        {
            return Failed(ex == null ? null : ex.GetType().Name, ex == null ? null : ex.Message);
        }

        /// <summary>
        /// The step failed with this exception type and message (either may be empty: a failure that is
        /// not an exception passes its plain reason as the message). Returns the line to log, or null.
        /// </summary>
        public string Failed(string type, string message)
        {
            double now = _now();
            string detail = Detail(type, message);
            bool first = !Failing;
            bool known = _kindsSaid.Contains(detail) || _kindsSaid.Count >= MaxKinds;

            Failing = true;
            FailuresInARow++;

            string line = null;
            if (first)
            {
                line = Capital(_name) + " failed, and is tried again each time: " + detail + ".";
            }
            else if (!known && now - _lastLineAt >= NewKindGapSeconds)
            {
                line = Capital(_name) + " failed in a new way" + Since() + ": " + detail + ".";
            }
            else if (known && now - _lastLineAt >= RepeatSeconds)
            {
                line = Capital(_name) + " is still failing" + Since() + ": " + detail + ".";
            }

            if (line == null)
            {
                _unsaid++;
                return null;
            }

            if (_kindsSaid.Count < MaxKinds)
            {
                _kindsSaid.Add(detail);
            }

            _lastLineAt = now;
            _unsaid = 0;
            return line;
        }

        private string Since()
        {
            return _unsaid > 0 ? " (" + Count(_unsaid) + " since the last line)" : string.Empty;
        }

        private static string Detail(string type, string message)
        {
            string text;
            if (string.IsNullOrEmpty(message))
            {
                text = string.IsNullOrEmpty(type) ? "no reason given" : type;
            }
            else
            {
                text = string.IsNullOrEmpty(type) ? message : type + ": " + message;
            }

            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]))
                {
                    chars[i] = ' ';
                }
            }

            text = new string(chars).Trim();
            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            return text.Length > MaxDetail ? text.Substring(0, MaxDetail) : text;
        }

        private static string Count(int n)
        {
            return n == 1 ? "1 failure" : n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " failures";
        }

        private static string Capital(string s)
        {
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
