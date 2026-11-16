using System.Globalization;
using BuckCalculator;

int checks = 0;

void Check(bool condition, string message)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException(message);
}

void Near(double actual, double expected, string message, double tolerance = 1e-9)
{
    Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected)),
        $"{message}: expected {expected:G17}, got {actual:G17}");
}

void Rejected(BuckInputs input, string message)
{
    try
    {
        BuckMath.Calculate(input);
    }
    catch (ArgumentException exception)
    {
        Check(!string.IsNullOrWhiteSpace(exception.Message), message + " needs an error message");
        return;
    }
    throw new InvalidOperationException(message + " was accepted");
}

var baseline = BuckMath.Calculate(new BuckInputs());
Near(baseline.DutyMin, 0.3125, "Minimum duty");
Near(baseline.DutyMax, 5.0 / 9, "Maximum duty");
Near(baseline.RequiredInductanceUh, 35.80729166666667, "Tolerance-aware required inductance");
Near(baseline.SelectedInductanceUh, 39, "E12 automatic inductance");
Near(baseline.MinimumInductanceUh, 31.2, "Minimum actual inductance");
Near(baseline.CurrentRipple, 0.5508814102564102, "Worst-case ripple");
Near(baseline.PeakCurrent, 2.2754407051282053, "Peak inductor current");
Near(baseline.InductorRms, 2.006312337103533, "RMS inductor current");
Near(baseline.SelectedOutputCapacitanceUf, 27, "E12 automatic capacitor");
Near(baseline.RequiredOutputCapacitanceUf, 24.955714949471478, "ESR and derating-aware capacitance");
Near(baseline.TotalOutputRippleMv, 18.694262672127255, "Conservative output ripple");
Near(baseline.InputCapacitanceUf, 12.5, "Interior worst-case input capacitance");
Near(baseline.InputCapacitorRms, 1.003343989361307, "Interior exact input capacitor RMS", 1e-8);
Check(baseline.ValleyCurrent > 0, "Full-load CCM");
Check(baseline.Metrics.Count >= 15, "Engineering result rows");
Check(baseline.Notes.Count > 0, "Model notes");

// The local course uses 33 µH with −20% tolerance as a manual candidate.
var course = BuckMath.Calculate(new BuckInputs { InductorUh = 33 });
Near(course.CurrentRipple, 0.6510416666666665, "Course manual-L ripple");
Near(course.PeakCurrent, 2.325520833333333, "Course peak current");
Near(course.InductorRms, 2.008810909712014, "Course RMS current");
Check(course.Notes.Any(note => note.Contains("纹波") && (note.Contains("超") || note.Contains("超过"))),
    "Manual candidate must report exceeded ripple target");

var smallCapacitor = BuckMath.Calculate(new BuckInputs { OutputCapacitorUf = 10 });
Check(smallCapacitor.TotalOutputRippleMv > 20, "Manual capacitor yields exceeded ripple");
Check(smallCapacitor.Notes.Any(note => note.Contains("纹波") && (note.Contains("超") || note.Contains("超过"))),
    "Manual capacitor failure is visible");

var lightLoad = BuckMath.Calculate(new BuckInputs { IoutOperating = 0.05 });
Near(lightLoad.PeakCurrent, baseline.PeakCurrent, "Design remains based on rated load");
Check(lightLoad.Notes.Any(note => note.Contains("DCM")), "Light-load DCM warning");
var boundary = BuckMath.Calculate(new BuckInputs { IoutOperating = baseline.BoundaryCurrent });
Check(boundary.OperatingMode.Contains("临界"), "Boundary mode");
var noLoad = BuckMath.Calculate(new BuckInputs { IoutOperating = 0 });
Check(noLoad.Notes.Any(note => note.Contains("DCM")), "Zero-load applicability");

var noEsr = BuckMath.Calculate(new BuckInputs { EsrMilliohm = 0, CapacitanceDeratingPercent = 0, InductorTolerancePercent = 0 });
Near(noEsr.EsrRippleMv, 0, "Zero ESR accepted");
Near(noEsr.EffectiveOutputCapacitanceUf, noEsr.SelectedOutputCapacitanceUf, "Zero derating accepted");
var fixedInput = BuckMath.Calculate(new BuckInputs { VinMin = 12, VinMax = 12 });
Near(fixedInput.DutyMin, fixedInput.DutyMax, "Single-point voltage range accepted");

Rejected(new BuckInputs { VinMin = 5 }, "Buck voltage ordering");
Rejected(new BuckInputs { VinMax = 8 }, "Reversed voltage range");
Rejected(new BuckInputs { Vout = 0 }, "Zero output voltage");
Rejected(new BuckInputs { IoutMax = -1 }, "Negative output current");
Rejected(new BuckInputs { IoutOperating = -0.1 }, "Negative operating load");
Rejected(new BuckInputs { IoutOperating = 3 }, "Operating load exceeds rating");
Rejected(new BuckInputs { FrequencyKhz = 0 }, "Zero frequency");
Rejected(new BuckInputs { RipplePercent = 0 }, "Zero ripple target");
Rejected(new BuckInputs { RipplePercent = 200 }, "Non-CCM ripple target");
Rejected(new BuckInputs { InductorTolerancePercent = 100 }, "Zero minimum inductance");
Rejected(new BuckInputs { CapacitanceDeratingPercent = 100 }, "Zero effective capacitance");
Rejected(new BuckInputs { EsrMilliohm = -1 }, "Negative ESR");
Rejected(new BuckInputs { EsrMilliohm = 1000 }, "Exhausted ESR budget");
Rejected(new BuckInputs { InductorUh = -1 }, "Negative candidate inductance");
Rejected(new BuckInputs { InductorUh = 1 }, "Rated-load DCM");
Rejected(new BuckInputs { OutputCapacitorUf = -1 }, "Negative candidate capacitance");
Rejected(new BuckInputs { OutputRippleMv = 0 }, "Zero output ripple budget");
Rejected(new BuckInputs { InputRippleMv = 0 }, "Zero input ripple budget");
Rejected(new BuckInputs { VinMin = double.NaN }, "NaN");
Rejected(new BuckInputs { FrequencyKhz = double.PositiveInfinity }, "Positive infinity");
Rejected(new BuckInputs { EsrMilliohm = double.NegativeInfinity }, "Negative infinity");
Rejected(new BuckInputs { FrequencyKhz = double.Epsilon }, "Underflow frequency");
Rejected(new BuckInputs { Vout = 1e200, VinMin = 2e200, VinMax = 3e200, IoutMax = 1e200 }, "Overflow results");

// Independently scan duty intervals to catch missed interior current maxima.
var random = new Random(20261116);
for (int scenario = 0; scenario < 100; scenario++)
{
    double output = 1 + random.NextDouble() * 24;
    double inputMinimum = output * (1.05 + random.NextDouble() * 3);
    double inputMaximum = inputMinimum * (1 + random.NextDouble() * 3);
    double ratedCurrent = 0.1 + random.NextDouble() * 8;
    var input = new BuckInputs
    {
        VinMin = inputMinimum,
        VinMax = inputMaximum,
        Vout = output,
        IoutMax = ratedCurrent,
        IoutOperating = ratedCurrent,
        FrequencyKhz = 50 + random.NextDouble() * 950,
        RipplePercent = 10 + random.NextDouble() * 80,
        InductorTolerancePercent = random.NextDouble() * 40,
        EsrMilliohm = 0
    };
    var result = BuckMath.Calculate(input);
    double sampledInputRms = 0;
    double sampledHighSideRms = 0;
    double sampledLowSideRms = 0;
    double sampledInputCapacitance = 0;
    double frequency = input.FrequencyKhz * 1000;
    double inductance = result.MinimumInductanceUh * 1e-6;
    for (int sample = 0; sample <= 4000; sample++)
    {
        double duty = result.DutyMin + (result.DutyMax - result.DutyMin) * sample / 4000;
        double ripple = output * (1 - duty) / (inductance * frequency);
        double meanSquare = ratedCurrent * ratedCurrent + ripple * ripple / 12;
        sampledInputRms = Math.Max(sampledInputRms, Math.Sqrt(ratedCurrent * ratedCurrent * duty * (1 - duty) + duty * ripple * ripple / 12));
        sampledHighSideRms = Math.Max(sampledHighSideRms, Math.Sqrt(duty * meanSquare));
        sampledLowSideRms = Math.Max(sampledLowSideRms, Math.Sqrt((1 - duty) * meanSquare));
        sampledInputCapacitance = Math.Max(sampledInputCapacitance, ratedCurrent * duty * (1 - duty) / (frequency * input.InputRippleMv * 1e-3) * 1e6);
    }
    Check(result.InputCapacitorRms >= sampledInputRms - 1e-10, "Input RMS covers entire interval");
    Near(result.InputCapacitorRms, sampledInputRms, "Input RMS dense scan", 1e-6);
    Near(result.HighSideRmsWorst, sampledHighSideRms, "High-side RMS dense scan", 1e-6);
    Near(result.LowSideRmsWorst, sampledLowSideRms, "Low-side RMS dense scan", 1e-6);
    Near(result.InputCapacitanceUf, sampledInputCapacitance, "Input capacitance dense scan", 1e-6);
    Check(result.CurrentRipple <= ratedCurrent * input.RipplePercent / 100 * (1 + 1e-10), "Auto L meets target after tolerance");
    Check(result.TotalOutputRippleMv <= input.OutputRippleMv * (1 + 1e-10), "Auto C meets budget after derating");
}

string markdown = ReportExporter.ToMarkdown(course);
string csv = ReportExporter.ToCsv(course);
Check(markdown.Contains("33") && markdown.Contains("µH") && markdown.Contains("9") && markdown.Contains("16"), "Report contains candidate and range");
Check(markdown.Contains("CCM"), "Report explains model");
Check(csv.StartsWith("\"分组\",\"参数\""), "CSV headers");
Check(csv.Contains("输入电压下限") && csv.Contains("工程检查"), "CSV includes inputs and checks");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
string localeReport = ReportExporter.ToMarkdown(new BuckResult { Inputs = new BuckInputs { VinMin = 9.5 } });
Check(localeReport.Contains("9.5"), "Report decimal format across locales");

Console.WriteLine($"PASS {checks} checks");
