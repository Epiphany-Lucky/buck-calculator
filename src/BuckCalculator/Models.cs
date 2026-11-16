namespace BuckCalculator;

public sealed record BuckInputs
{
    public double VinMin { get; init; } = 9;
    public double VinMax { get; init; } = 16;
    public double Vout { get; init; } = 5;
    public double IoutMax { get; init; } = 2;
    public double IoutOperating { get; init; } = 2;
    public double FrequencyKhz { get; init; } = 200;
    public double RipplePercent { get; init; } = 30;
    public double OutputRippleMv { get; init; } = 20;
    public double InductorTolerancePercent { get; init; } = 20;
    public double CapacitanceDeratingPercent { get; init; } = 20;
    public double EsrMilliohm { get; init; } = 5;
    public double InductorUh { get; init; }
    public double OutputCapacitorUf { get; init; }
    public double InputRippleMv { get; init; } = 200;
}

public sealed record ResultMetric(string Section, string Label, string Value, string Unit, string Detail);

public sealed record BuckResult
{
    public required BuckInputs Inputs { get; init; }
    public double DutyMin { get; init; }
    public double DutyMax { get; init; }
    public double RequiredInductanceUh { get; init; }
    public double SelectedInductanceUh { get; init; }
    public double MinimumInductanceUh { get; init; }
    public double CurrentRipple { get; init; }
    public double PeakCurrent { get; init; }
    public double ValleyCurrent { get; init; }
    public double InductorRms { get; init; }
    public double BoundaryCurrent { get; init; }
    public string OperatingMode { get; init; } = "";
    public bool FullLoadCcm { get; init; }
    public bool MeetsRippleTargets { get; init; }
    public double RequiredOutputCapacitanceUf { get; init; }
    public double SelectedOutputCapacitanceUf { get; init; }
    public double EffectiveOutputCapacitanceUf { get; init; }
    public double CapacitiveRippleMv { get; init; }
    public double EsrRippleMv { get; init; }
    public double TotalOutputRippleMv { get; init; }
    public double InputCapacitanceUf { get; init; }
    public double InputCapacitorRms { get; init; }
    public double HighSideRmsWorst { get; init; }
    public double LowSideRmsWorst { get; init; }
    public double OutputPower { get; init; }
    public double LoadResistance { get; init; }
    public IReadOnlyList<ResultMetric> Metrics { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}
