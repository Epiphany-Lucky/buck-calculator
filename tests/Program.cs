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
        if (input.Topology == ConverterTopology.Boost)
            BoostMath.Calculate(input);
        else
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
Near(baseline.WaveformVin, 16, "Buck waveform input voltage");
Near(baseline.WaveformDuty, baseline.DutyMin, "Buck waveform duty");
Near(baseline.WaveformAverageCurrent, 2, "Buck waveform average");
Near(baseline.WaveformRipple, baseline.CurrentRipple, "Buck same-point waveform ripple");
Near(baseline.WaveformPeakCurrent, baseline.PeakCurrent, "Buck same-point waveform peak");
Near(baseline.WaveformValleyCurrent, baseline.ValleyCurrent, "Buck same-point waveform valley");
Near(baseline.MainSwitchRms, baseline.HighSideRmsWorst, "Buck compatible main switch RMS");
Near(baseline.RectifierRms, baseline.LowSideRmsWorst, "Buck compatible rectifier RMS");
Near(baseline.OutputCapacitorRms, baseline.CurrentRipple / Math.Sqrt(12), "Buck output capacitor RMS");
Near(baseline.MaximumRelativeRipplePercent, baseline.CurrentRipple / 2 * 100, "Buck relative ripple");
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
Rejected(new BuckInputs { Topology = (ConverterTopology)42 }, "Undefined topology");

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

var boostInput = new BuckInputs
{
    Topology = ConverterTopology.Boost,
    VinMin = 9,
    VinMax = 16,
    Vout = 24,
    OutputRippleMv = 100
};
var boost = BoostMath.Calculate(boostInput);
Near(boost.DutyMin, 1.0 / 3, "Boost minimum duty");
Near(boost.DutyMax, 0.625, "Boost maximum duty");
Near(boost.RequiredInductanceUh, 37.03703703703704, "Boost tolerance-aware required L");
Near(boost.SelectedInductanceUh, 39, "Boost E12 L");
Near(boost.MinimumInductanceUh, 31.2, "Boost minimum actual L");
Near(boost.CurrentRipple, 0.9615384615384615, "Boost absolute ripple");
Near(boost.RippleVin, 12, "Boost interior absolute-ripple worst point");
Near(boost.MaximumRelativeRipplePercent, 28.49002849002849, "Boost relative ripple uses local average");
Near(boost.BoundaryCurrent, 0.2849002849002849, "Boost output boundary");
Near(boost.BoundaryVin, 16, "Boost relative-ripple boundary voltage");
Near(boost.PeakCurrent, 5.784054487179487, "Boost peak");
Near(boost.PeakCurrentVin, 9, "Boost peak worst voltage");
Near(boost.ValleyCurrent, 2.5726495726495724, "Boost minimum valley");
Near(boost.ValleyCurrentVin, 16, "Boost minimum valley voltage");
Near(boost.InductorRms, 5.3396779831702, "Boost inductor RMS");
Near(boost.MainSwitchRms, 4.221386099668018, "Boost low-side switch RMS");
Near(boost.RectifierRms, 3.2698716123851432, "Boost rectifier RMS");
Near(boost.OutputCapacitorRms, 2.5869016914993535, "Boost output capacitor RMS");
Near(boost.InputCapacitorRms, 0.2775722448027047, "Boost input capacitor RMS");
Near(boost.RequiredOutputCapacitanceUf, 109.91178874390553, "Boost ESR and derating-aware C");
Near(boost.SelectedOutputCapacitanceUf, 120, "Boost E12 output C");
Near(boost.TotalOutputRippleMv, 94.02443910256412, "Boost conservative output ripple");
Near(boost.WaveformVin, 9, "Boost waveform point");
Near(boost.WaveformAverageCurrent, 16.0 / 3, "Boost waveform mean");
Near(boost.WaveformRipple, 0.9014423076923075, "Boost waveform uses its local ripple");
Near(boost.WaveformPeakCurrent - boost.WaveformValleyCurrent, boost.WaveformRipple, "Boost waveform is a consistent triangle");
Near((boost.WaveformPeakCurrent + boost.WaveformValleyCurrent) / 2, boost.WaveformAverageCurrent, "Boost waveform average is consistent");
Check(boost.WaveformRipple < boost.CurrentRipple, "Boost global extrema remain separate from waveform");
Check(boost.HighSideRmsWorst == 0 && boost.LowSideRmsWorst == 0, "Boost avoids ambiguous buck switch fields");
Check(boost.MeetsRippleTargets && boost.FullLoadCcm, "Boost automatic components satisfy rated design");
Check(boost.Metrics.Any(metric => metric.Label == "二极管平均电流" && metric.Value == "2"), "Boost diode average current");
Check(boost.Metrics.Count(metric => metric.Label.Contains("理想") && metric.Value == "24"
    && metric.Detail.Contains("寄生尖峰")) == 2, "Boost ideal voltage stresses include margin instructions");

var boostFixed = BoostMath.Calculate(boostInput with { VinMin = 12, VinMax = 12 });
Near(boostFixed.DutyMin, boostFixed.DutyMax, "Boost single-point input");
var boostLight = BoostMath.Calculate(boostInput with { IoutOperating = 0.05 });
Check(boostLight.OperatingMode.Contains("DCM"), "Boost light-load DCM");
Near(boostLight.PeakCurrent, boost.PeakCurrent, "Boost design remains rated load");
var boostBoundary = BoostMath.Calculate(boostInput with { IoutOperating = boost.BoundaryCurrent });
Check(boostBoundary.OperatingMode.Contains("临界"), "Boost boundary mode");
Check(BoostMath.Calculate(boostInput with { IoutOperating = 0 }).OperatingMode.Contains("DCM"), "Boost no-load applicability");
var boostSmallCap = BoostMath.Calculate(boostInput with { OutputCapacitorUf = 10 });
Check(!boostSmallCap.MeetsRippleTargets && boostSmallCap.Notes.Any(note => note.Contains("超过")), "Boost manual-C target warning");
var boostSmallL = BoostMath.Calculate(boostInput with { InductorUh = 10 });
Check(boostSmallL.MaximumRelativeRipplePercent > boostInput.RipplePercent
    && !boostSmallL.MeetsRippleTargets, "Boost manual-L relative-ripple warning");

// Modest step-up and large ripple make the capacitor discharge during part of the off interval.
var correctedInput = boostInput with
{
    VinMin = 22, VinMax = 23, InductorUh = 3.3, InductorTolerancePercent = 0,
    CapacitanceDeratingPercent = 0, EsrMilliohm = 0, OutputCapacitorUf = 100
};
var correctedBoost = BoostMath.Calculate(correctedInput);
Near(correctedBoost.CapacitiveRippleMv, 20.3537457912458, "Boost exact off-interval capacitor discharge correction");
Check(correctedBoost.CapacitiveRippleMv > 2 * (2 * (1 - 22.0 / 24) / 200000 / 100e-6 * 1e3),
    "Boost correction catches traditional Iout-D estimate underflow");
var strongCorrection = BoostMath.Calculate(correctedInput with
{
    VinMin = 23, VinMax = 23, IoutMax = 1, IoutOperating = 1,
    FrequencyKhz = 100, InductorUh = 5
});
Check(strongCorrection.FullLoadCcm && strongCorrection.MaximumRelativeRipplePercent < 200,
    "Boost low-duty high-ripple regression remains CCM");
Check(strongCorrection.CapacitiveRippleMv > 5 * (1 * (1 - 23.0 / 24) / 100000 / 100e-6 * 1e3),
    "Boost high-ripple correction covers substantial off-interval discharge");

var interiorBoost = BoostMath.Calculate(boostInput with
{
    VinMin = 4.8, VinMax = 22.8, InductorUh = 6, InductorTolerancePercent = 0,
    EsrMilliohm = 0, RipplePercent = 150
});
Near(interiorBoost.CurrentRipple, 5, "Boost interior absolute ripple");
Near(interiorBoost.RippleVin, 12, "Boost absolute-ripple interior voltage");
Near(interiorBoost.BoundaryVin, 16, "Boost boundary interior voltage");
Near(interiorBoost.ValleyCurrent, 0.7570374790039036, "Boost interior minimum valley");
Near(interiorBoost.ValleyCurrentVin, 16.8620042652255, "Boost interior minimum-valley voltage");
Near(interiorBoost.WaveformRipple, 3.2, "Boost interior-extrema case waveform ripple");
Near(interiorBoost.WaveformValleyCurrent, 8.4, "Boost waveform never combines independent extrema");

var transitionInput = boostInput with
{
    VinMin = 19.2, VinMax = 19.2, InductorUh = 19.2, InductorTolerancePercent = 0,
    CapacitanceDeratingPercent = 0, EsrMilliohm = 0, OutputCapacitorUf = 100
};
var transition = BoostMath.Calculate(transitionInput);
Near(transition.CapacitiveRippleMv, 20, "Boost capacitor branch transition");
var transitionBelow = BoostMath.Calculate(transitionInput with { InductorUh = 19.2 * (1 - 1e-7) });
var transitionAbove = BoostMath.Calculate(transitionInput with { InductorUh = 19.2 * (1 + 1e-7) });
Near(transitionBelow.CapacitiveRippleMv, transitionAbove.CapacitiveRippleMv, "Boost capacitor formula continuity", 1e-7);
var tinyDuty = BoostMath.Calculate(boostInput with
{
    VinMin = 1, VinMax = 1, Vout = 1.000000000001, EsrMilliohm = 0
});
Check(tinyDuty.OutputCapacitorRms > 0 && double.IsFinite(tinyDuty.OutputCapacitorRms), "Boost low-duty capacitor RMS stays finite and positive");

Rejected(boostInput with { VinMin = 0 }, "Boost zero input");
Rejected(boostInput with { VinMax = 24 }, "Boost equal input and output");
Rejected(boostInput with { VinMax = 25 }, "Boost input exceeds output");
Rejected(boostInput with { VinMin = 17 }, "Boost reversed input range");
Rejected(boostInput with { Vout = 0 }, "Boost zero output");
Rejected(boostInput with { IoutMax = -1 }, "Boost negative rated current");
Rejected(boostInput with { IoutOperating = -1 }, "Boost negative operating current");
Rejected(boostInput with { IoutOperating = 3 }, "Boost operating current exceeds rating");
Rejected(boostInput with { FrequencyKhz = 0 }, "Boost zero frequency");
Rejected(boostInput with { RipplePercent = 0 }, "Boost zero ripple target");
Rejected(boostInput with { RipplePercent = 200 }, "Boost invalid CCM ripple target");
Rejected(boostInput with { InductorTolerancePercent = 100 }, "Boost zero effective L");
Rejected(boostInput with { CapacitanceDeratingPercent = 100 }, "Boost zero effective C");
Rejected(boostInput with { InductorUh = 1 }, "Boost rated DCM");
Rejected(boostInput with { InductorUh = -1 }, "Boost negative L");
Rejected(boostInput with { OutputCapacitorUf = -1 }, "Boost negative C");
Rejected(boostInput with { EsrMilliohm = -1 }, "Boost negative ESR");
Rejected(boostInput with { EsrMilliohm = 1000 }, "Boost exhausted ESR budget");
Rejected(boostInput with { OutputRippleMv = 0 }, "Boost zero output ripple");
Rejected(boostInput with { InputRippleMv = 0 }, "Boost zero input ripple");
Rejected(boostInput with { VinMin = double.NaN }, "Boost NaN");
Rejected(boostInput with { FrequencyKhz = double.PositiveInfinity }, "Boost infinite frequency");
Rejected(boostInput with { FrequencyKhz = double.Epsilon }, "Boost frequency underflow");
Rejected(boostInput with { Vout = 1e200, VinMin = 1e199, VinMax = 2e199, IoutMax = 1e200 }, "Boost overflowing results");
Rejected(boostInput with { VinMin = double.Epsilon }, "Boost unrepresentable voltage ratio");

for (int scenario = 0; scenario < 100; scenario++)
{
    double output = 5 + random.NextDouble() * 60;
    double minimumRatio = 0.05 + random.NextDouble() * 0.7;
    double maximumRatio = minimumRatio + random.NextDouble() * (0.99 - minimumRatio);
    double ratedCurrent = 0.1 + random.NextDouble() * 8;
    var input = boostInput with
    {
        VinMin = output * minimumRatio, VinMax = output * maximumRatio, Vout = output,
        IoutMax = ratedCurrent, IoutOperating = ratedCurrent,
        FrequencyKhz = 50 + random.NextDouble() * 950, RipplePercent = 10 + random.NextDouble() * 175,
        InductorTolerancePercent = random.NextDouble() * 40, CapacitanceDeratingPercent = random.NextDouble() * 60,
        EsrMilliohm = 0
    };
    var result = BoostMath.Calculate(input);
    double frequency = input.FrequencyKhz * 1000;
    double inductance = result.MinimumInductanceUh * 1e-6;
    double maxRipple = 0, maxRelativeRipple = 0, maxPeak = 0, minValley = double.PositiveInfinity;
    double maxIlRms = 0, maxSwitchRms = 0, maxRectifierRms = 0, maxCoutRms = 0, maxBoundary = 0, maxCharge = 0;
    for (int sample = 0; sample <= 4000; sample++)
    {
        double x = minimumRatio + (maximumRatio - minimumRatio) * sample / 4000;
        double duty = 1 - x;
        double ripple = output * x * duty / (frequency * inductance);
        double average = ratedCurrent / x;
        double peak = average + ripple / 2;
        double valley = average - ripple / 2;
        double meanSquare = average * average + ripple * ripple / 12;
        // Integrate the positive linear capacitor current over the diode-conduction interval.
        double offTime = x / frequency;
        double slope = ripple / offTime;
        double chargingTime = Math.Clamp((peak - ratedCurrent) / slope, 0, offTime);
        double charge = (peak - ratedCurrent) * chargingTime - slope * chargingTime * chargingTime / 2;
        maxRipple = Math.Max(maxRipple, ripple);
        maxRelativeRipple = Math.Max(maxRelativeRipple, ripple / average * 100);
        maxPeak = Math.Max(maxPeak, peak);
        minValley = Math.Min(minValley, valley);
        maxIlRms = Math.Max(maxIlRms, Math.Sqrt(meanSquare));
        maxSwitchRms = Math.Max(maxSwitchRms, Math.Sqrt(duty * meanSquare));
        maxRectifierRms = Math.Max(maxRectifierRms, Math.Sqrt(x * meanSquare));
        maxCoutRms = Math.Max(maxCoutRms, Math.Sqrt(ratedCurrent * ratedCurrent * duty / x + x * ripple * ripple / 12));
        maxBoundary = Math.Max(maxBoundary, x * ripple / 2);
        maxCharge = Math.Max(maxCharge, charge);
    }
    Check(result.CurrentRipple >= maxRipple - 1e-10, "Boost absolute ripple covers range");
    Near(result.CurrentRipple, maxRipple, "Boost absolute ripple dense scan", 1e-6);
    Check(result.MaximumRelativeRipplePercent >= maxRelativeRipple - 1e-9, "Boost relative ripple covers range");
    Near(result.MaximumRelativeRipplePercent, maxRelativeRipple, "Boost local relative ripple dense scan", 1e-6);
    Near(result.PeakCurrent, maxPeak, "Boost peak dense scan", 1e-6);
    Check(result.ValleyCurrent <= minValley + 1e-10, "Boost valley covers interior minimum");
    Near(result.ValleyCurrent, minValley, "Boost valley dense scan", 1e-5);
    Near(result.InductorRms, maxIlRms, "Boost inductor RMS dense scan", 1e-6);
    Near(result.MainSwitchRms, maxSwitchRms, "Boost main switch RMS dense scan", 1e-6);
    Near(result.RectifierRms, maxRectifierRms, "Boost rectifier RMS dense scan", 1e-6);
    Near(result.OutputCapacitorRms, maxCoutRms, "Boost output capacitor RMS dense scan", 1e-6);
    Near(result.BoundaryCurrent, maxBoundary, "Boost output boundary dense scan", 1e-6);
    Near(result.InputCapacitanceUf, maxRipple / (8 * frequency * input.InputRippleMv * 1e-3) * 1e6, "Boost input C dense scan", 1e-6);
    Near(result.InputCapacitorRms, maxRipple / Math.Sqrt(12), "Boost input capacitor RMS dense scan", 1e-6);
    Near(result.CapacitiveRippleMv, maxCharge / (result.EffectiveOutputCapacitanceUf * 1e-6) * 1e3, "Boost positive-charge integration dense scan", 1e-6);
    Check(result.MaximumRelativeRipplePercent <= input.RipplePercent * (1 + 1e-10), "Boost automatic L meets full-range target");
    Check(result.TotalOutputRippleMv <= input.OutputRippleMv * (1 + 1e-10), "Boost automatic C meets budget");
    Near(result.WaveformPeakCurrent - result.WaveformValleyCurrent, result.WaveformRipple, "Boost random consistent waveform", 1e-9);
}

// Independently integrate the full switching-period capacitor current to verify both Qpp branches.
foreach (var result in new[] { boost, correctedBoost, strongCorrection, transition, interiorBoost })
{
    const int samples = 200000;
    double charge = 0, minimumCharge = 0, maximumCharge = 0;
    double step = 1.0 / (result.Inputs.FrequencyKhz * 1000 * samples);
    double duty = result.WaveformDuty;
    for (int sample = 0; sample < samples; sample++)
    {
        double phase = (sample + 0.5) / samples;
        double capacitorCurrent = phase < duty ? -result.Inputs.IoutMax
            : result.WaveformPeakCurrent - result.WaveformRipple * ((phase - duty) / (1 - duty)) - result.Inputs.IoutMax;
        charge += capacitorCurrent * step;
        minimumCharge = Math.Min(minimumCharge, charge);
        maximumCharge = Math.Max(maximumCharge, charge);
    }
    double integratedRipple = (maximumCharge - minimumCharge) / (result.EffectiveOutputCapacitanceUf * 1e-6) * 1e3;
    Near(result.CapacitiveRippleMv, integratedRipple, "Boost full-cycle capacitor current integration", 2e-5);
}

string boostMarkdown = ReportExporter.ToMarkdown(boost);
string boostCsv = ReportExporter.ToCsv(boost);
Check(boostMarkdown.StartsWith("# BOOST 电路计算报告"), "Boost report title");
Check(boostMarkdown.Contains("相对当地平均 IL") && boostMarkdown.Contains("Qpp"), "Boost report ripple basis and exact capacitor formula");
Check(boostMarkdown.Contains("slva372d") && !boostMarkdown.Contains("Buck Power Stage"), "Boost report primary references");
Check(boostCsv.Contains("计算拓扑") && boostCsv.Contains("BOOST"), "Boost CSV records topology");
Check(markdown.StartsWith("# BUCK 电路计算报告") && csv.Contains("BUCK"), "Legacy buck report and topology remain valid");
Check(ReportExporter.GetFormulaText(ConverterTopology.Boost).Contains("1 − Vin / Vout"), "Shared boost formula text");
Check(ReportExporter.GetFormulaText(ConverterTopology.Buck).Contains("D = Vout / Vin"), "Shared buck formula text");

Console.WriteLine($"PASS {checks} checks");
