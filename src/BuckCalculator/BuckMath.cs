using System.Globalization;

namespace BuckCalculator;

/// <summary>Ideal, single-phase, steady-state CCM buck power-stage calculations.</summary>
public static class BuckMath
{
    private static readonly double[] E12 = [1.0, 1.2, 1.5, 1.8, 2.2, 2.7, 3.3, 3.9, 4.7, 5.6, 6.8, 8.2];

    public static BuckResult Calculate(BuckInputs p)
    {
        ArgumentNullException.ThrowIfNull(p);
        Validate(p);

        double frequency = Positive(p.FrequencyKhz * 1_000, "开关频率");
        double dutyMin = Positive(p.Vout / p.VinMax, "最小占空比");
        double dutyMax = Positive(p.Vout / p.VinMin, "最大占空比");
        if (dutyMax >= 1)
            throw new ArgumentException("输入电压与输出电压过于接近，超出浮点计算精度。请增加降压裕量。");

        double targetRipple = Positive(p.IoutMax * (p.RipplePercent / 100), "目标电感纹波");
        double inductanceFactor = 1 - p.InductorTolerancePercent / 100;
        double capacitanceFactor = 1 - p.CapacitanceDeratingPercent / 100;
        double requiredInductance = Positive(p.Vout * (1 - dutyMin) / frequency / targetRipple / inductanceFactor * 1e6, "所需标称电感");
        double selectedInductance = p.InductorUh > 0 ? p.InductorUh : RoundUpE12(requiredInductance);
        double minimumInductance = Positive(selectedInductance * inductanceFactor, "最小有效电感");
        double inductanceHenries = Positive(minimumInductance * 1e-6, "有效电感换算");
        double rippleScale = Positive(p.Vout / frequency / inductanceHenries, "电感纹波系数");
        double ripple = Positive(rippleScale * (1 - dutyMin), "最大电感纹波");
        double boundary = ripple / 2;
        double peak = Positive(p.IoutMax + boundary, "电感峰值电流");
        double valley = p.IoutMax - boundary;
        if (valley <= 0)
            throw new ArgumentException("候选电感使额定负载的谷值电流小于或等于零，超出本计算器的额定 CCM 模型。请增大电感或开关频率，使额定负载谷值电流大于零。");

        double currentSquared = Positive(p.IoutMax * p.IoutMax, "电流平方");
        double rippleSquaredFactor = Positive(rippleScale * rippleScale / 12, "纹波均方系数");
        double inductorRms = Positive(Math.Sqrt(currentSquared + ripple * ripple / 12), "电感 RMS 电流");
        double esrRipple = Finite(ripple * p.EsrMilliohm, "ESR 纹波");
        double remainingRippleMv = p.OutputRippleMv - esrRipple;
        if (remainingRippleMv <= 0)
            throw new ArgumentException("输出电容 ESR 已占满允许的输出纹波预算，增加电容量也无法满足当前目标。请减小 ESR、增大电感或调整纹波目标。");

        double requiredCapacitance = Positive(ripple / (8 * frequency) / (remainingRippleMv * 1e-3) / capacitanceFactor * 1e6, "所需标称输出电容");
        double selectedCapacitance = p.OutputCapacitorUf > 0 ? p.OutputCapacitorUf : RoundUpE12(requiredCapacitance);
        double effectiveCapacitance = Positive(selectedCapacitance * capacitanceFactor, "有效输出电容");
        double capacitanceFarads = Positive(effectiveCapacitance * 1e-6, "有效电容换算");
        double capacitiveRipple = Positive(ripple / (8 * frequency) / capacitanceFarads * 1e3, "电容充放电纹波");
        double totalRipple = Positive(capacitiveRipple + esrRipple, "总输出纹波");

        // The rectangular-current approximation for Cin reaches its maximum at D = 0.5.
        double capacitanceDuty = Math.Clamp(0.5, dutyMin, dutyMax);
        double inputCapacitance = Positive(p.IoutMax * capacitanceDuty * (1 - capacitanceDuty) / frequency / (p.InputRippleMv * 1e-3) * 1e6, "所需有效输入电容");

        // Include triangular inductor ripple in Cin RMS. The stationary duty can lie inside the input range.
        double scale = Math.Max(currentSquared, rippleSquaredFactor);
        double a = currentSquared / scale;
        double b = rippleSquaredFactor / scale;
        double stationaryInputDuty = (a + b) / (a + 2 * b + Math.Sqrt(a * a + a * b + b * b));
        double inputRmsSquared = MaximumOverDuties(
            d => currentSquared * d * (1 - d) + rippleSquaredFactor * d * (1 - d) * (1 - d),
            dutyMin, dutyMax, stationaryInputDuty);
        double inputRms = Positive(Math.Sqrt(inputRmsSquared), "输入电容 RMS 电流");

        // HS mean-square current is D[Io² + k²(1-D)²/12]. Its derivative is quadratic.
        double highSideSquared = MaximumOverDuties(
            d => d * (currentSquared + rippleSquaredFactor * (1 - d) * (1 - d)),
            dutyMin, dutyMax);
        if (b > 3 * a)
        {
            double root = Math.Sqrt(1 - 3 * a / b);
            highSideSquared = Math.Max(highSideSquared, MaximumOverDuties(
                d => d * (currentSquared + rippleSquaredFactor * (1 - d) * (1 - d)),
                dutyMin, dutyMax, (2 - root) / 3, (2 + root) / 3));
        }
        double highSideRms = Positive(Math.Sqrt(highSideSquared), "高侧开关 RMS 电流");
        // LS mean-square current is Io²(1-D) + k²(1-D)³/12, monotonically decreasing in D.
        double lowSideRms = Positive(Math.Sqrt((1 - dutyMin) * (currentSquared + rippleSquaredFactor * (1 - dutyMin) * (1 - dutyMin))), "低侧开关 RMS 电流");
        double outputPower = Positive(p.Vout * p.IoutMax, "输出功率");
        double loadResistance = Positive(p.Vout / p.IoutMax, "额定等效负载");

        bool inductorTargetMet = ripple <= targetRipple * (1 + 1e-9);
        bool capacitorTargetMet = totalRipple <= p.OutputRippleMv * (1 + 1e-9);
        double modeTolerance = boundary * 1e-9;
        string operatingMode = p.IoutOperating > boundary + modeTolerance ? "CCM（全输入范围）"
            : Math.Abs(p.IoutOperating - boundary) <= modeTolerance ? "临界导通（最高输入电压）"
            : "DCM（部分或全部输入范围）";

        var notes = new List<string>
        {
            "计算采用理想单相 BUCK 的稳态 CCM 模型，占空比 D = Vout / Vin。所有电流应力和纹波结果均按额定最大负载计算，电感纹波采用最高输入电压与负容差后的最小电感。",
            $"电感负容差为 {Format(p.InductorTolerancePercent)}%，输出电容有效容量折减为 {Format(p.CapacitanceDeratingPercent)}%。折减比例需结合实际器件的容差、直流偏压与温度确定。",
            "输入电容容量下限采用矩形输入电流近似，输入 ESR 与 ESL 未计入。输入电容 RMS 包含三角电感纹波，并检查输入电压区间内部的极值。",
            "输出纹波以电容充放电项与 ESR 项相加估算。实际器件的电感、电容、饱和电流、热额定电流、限流、最小导通时间及环路稳定性需核对对应数据手册。"
        };
        if (!inductorTargetMet)
            notes.Add($"候选电感的最坏电流纹波为额定电流的 {Format(ripple / p.IoutMax * 100)}%，超过目标 {Format(p.RipplePercent)}%。");
        if (!capacitorTargetMet)
            notes.Add($"候选输出电容的最坏输出纹波为 {Format(totalRipple)} mV，超过目标 {Format(p.OutputRippleMv)} mV。");
        if (operatingMode.StartsWith("DCM", StringComparison.Ordinal))
            notes.Add($"当前工作负载 {Format(p.IoutOperating)} A 低于最坏 CCM 边界 {Format(boundary)} A，二极管整流或二极管仿真控制下会在部分或全部输入范围进入 DCM。此时该工作负载的实际占空比和波形需采用 DCM 模型，当前显示波形仍为额定最大负载的 CCM 波形。强制同步 CCM 控制可能产生反向电流。");
        else if (operatingMode.StartsWith("临界", StringComparison.Ordinal))
            notes.Add("当前工作负载在最高输入电压处达到 CCM/DCM 边界，波形与应力表仍按额定最大负载计算。");
        else
            notes.Add($"当前工作负载 {Format(p.IoutOperating)} A 大于最坏 CCM 边界 {Format(boundary)} A，理想模型下覆盖全输入范围的 CCM。波形与应力表按额定最大负载计算。");

        var metrics = new List<ResultMetric>
        {
            new("工作点", "最小占空比", Format(dutyMin * 100), "%", $"Vin = {Format(p.VinMax)} V，理想 CCM"),
            new("工作点", "最大占空比", Format(dutyMax * 100), "%", $"Vin = {Format(p.VinMin)} V，理想 CCM"),
            new("工作点", "额定输出功率", Format(outputPower), "W", "按最大输出电流计算"),
            new("工作点", "额定等效负载", Format(loadResistance), "Ω", "Vout / Iout,max"),
            new("电感", "所需标称电感下限", Format(requiredInductance), "µH", "最高输入电压，包含电感负容差"),
            new("电感", "采用标称电感", Format(selectedInductance), "µH", p.InductorUh > 0 ? "使用手动候选值" : "E12 标称系列向上取值"),
            new("电感", "最小有效电感", Format(minimumInductance), "µH", "标称值 × (1 − 负容差比例)"),
            new("电感", "最坏电感纹波", Format(ripple), "A p-p", $"额定电流的 {Format(ripple / p.IoutMax * 100)}%"),
            new("电感", "电感峰值电流", Format(peak), "A", "额定最大负载，最高输入电压"),
            new("电感", "电感谷值电流", Format(valley), "A", "额定最大负载，最高输入电压"),
            new("电感", "电感 RMS 电流", Format(inductorRms), "A RMS", "包含三角电流纹波"),
            new("电感", "最坏 CCM 边界电流", Format(boundary), "A", "ΔIL,max / 2，按最小有效电感计算"),
            new("电感", "工作负载导通模式", operatingMode, "", "按二极管/二极管仿真整流模型判断"),
            new("输出电容", "所需标称输出电容下限", Format(requiredCapacitance), "µF", "扣除 ESR 纹波预算，包含有效容量折减"),
            new("输出电容", "采用标称输出电容", Format(selectedCapacitance), "µF", p.OutputCapacitorUf > 0 ? "使用手动候选值" : "E12 标称系列向上取值"),
            new("输出电容", "有效输出电容", Format(effectiveCapacitance), "µF", "标称容量 × (1 − 折减比例)"),
            new("输出电容", "电容充放电纹波", Format(capacitiveRipple), "mV p-p", "ΔIL / (8 × fs × Cout,eff)"),
            new("输出电容", "ESR 纹波", Format(esrRipple), "mV p-p", "ΔIL × ESR"),
            new("输出电容", "总输出纹波估算", Format(totalRipple), "mV p-p", "充放电项与 ESR 项相加"),
            new("输入与开关", "有效输入电容下限", Format(inputCapacitance), "µF", $"矩形电流近似，最坏 D = {Format(capacitanceDuty * 100)}%，实际选型需考虑降额与输入 ESR"),
            new("输入与开关", "输入电容 RMS 电流", Format(inputRms), "A RMS", "包含电感纹波，搜索全输入范围的极值"),
            new("输入与开关", "高侧开关 RMS 电流", Format(highSideRms), "A RMS", "搜索全输入范围的极值"),
            new("输入与开关", "低侧开关 RMS 电流", Format(lowSideRms), "A RMS", "搜索全输入范围的极值")
        };

        return new BuckResult
        {
            Inputs = p,
            DutyMin = dutyMin,
            DutyMax = dutyMax,
            RequiredInductanceUh = requiredInductance,
            SelectedInductanceUh = selectedInductance,
            MinimumInductanceUh = minimumInductance,
            CurrentRipple = ripple,
            PeakCurrent = peak,
            ValleyCurrent = valley,
            InductorRms = inductorRms,
            BoundaryCurrent = boundary,
            OperatingMode = operatingMode,
            FullLoadCcm = true,
            MeetsRippleTargets = inductorTargetMet && capacitorTargetMet,
            RequiredOutputCapacitanceUf = requiredCapacitance,
            SelectedOutputCapacitanceUf = selectedCapacitance,
            EffectiveOutputCapacitanceUf = effectiveCapacitance,
            CapacitiveRippleMv = capacitiveRipple,
            EsrRippleMv = esrRipple,
            TotalOutputRippleMv = totalRipple,
            InputCapacitanceUf = inputCapacitance,
            InputCapacitorRms = inputRms,
            HighSideRmsWorst = highSideRms,
            LowSideRmsWorst = lowSideRms,
            OutputPower = outputPower,
            LoadResistance = loadResistance,
            Metrics = metrics,
            Notes = notes
        };
    }

    private static void Validate(BuckInputs p)
    {
        (double Value, string Name)[] positiveValues =
        [
            (p.VinMin, "最小输入电压"), (p.VinMax, "最大输入电压"), (p.Vout, "输出电压"),
            (p.IoutMax, "最大输出电流"), (p.FrequencyKhz, "开关频率"),
            (p.RipplePercent, "电感纹波比例"), (p.OutputRippleMv, "输出纹波目标"),
            (p.InputRippleMv, "输入纹波目标")
        ];
        foreach (var item in positiveValues)
            Positive(item.Value, item.Name);

        (double Value, string Name)[] nonnegativeValues =
        [
            (p.IoutOperating, "工作负载电流"), (p.InductorTolerancePercent, "电感负容差"),
            (p.CapacitanceDeratingPercent, "电容容量折减"), (p.EsrMilliohm, "输出电容 ESR"),
            (p.InductorUh, "候选电感"), (p.OutputCapacitorUf, "候选输出电容")
        ];
        foreach (var item in nonnegativeValues)
        {
            Finite(item.Value, item.Name);
            if (item.Value < 0)
                throw new ArgumentException($"{item.Name}必须大于或等于零。");
        }
        if (p.VinMin <= p.Vout)
            throw new ArgumentException("BUCK 降压计算要求最小输入电压大于输出电压。");
        if (p.VinMax < p.VinMin)
            throw new ArgumentException("最大输入电压必须大于或等于最小输入电压。");
        if (p.RipplePercent >= 200)
            throw new ArgumentException("额定 CCM 的电感纹波比例必须大于 0% 且小于 200%。");
        if (p.InductorTolerancePercent >= 100 || p.CapacitanceDeratingPercent >= 100)
            throw new ArgumentException("电感负容差与电容容量折减必须大于或等于 0% 且小于 100%。");
        if (p.IoutOperating > p.IoutMax)
            throw new ArgumentException("工作负载电流必须位于 0 至最大输出电流之间。");
    }

    private static double MaximumOverDuties(Func<double, double> value, double minimumDuty, double maximumDuty, params double[] stationaryDuties)
    {
        double maximum = Math.Max(value(minimumDuty), value(maximumDuty));
        foreach (double duty in stationaryDuties)
            maximum = Math.Max(maximum, value(Math.Clamp(duty, minimumDuty, maximumDuty)));
        return Finite(maximum, "区间极值");
    }

    private static double RoundUpE12(double value)
    {
        double decade = Positive(Math.Pow(10, Math.Floor(Math.Log10(value))), "E12 数值范围");
        foreach (double coefficient in E12)
        {
            double candidate = coefficient * decade;
            if (candidate >= value * (1 - 1e-12))
                return Positive(candidate, "E12 推荐值");
        }
        return Positive(10 * decade, "E12 推荐值");
    }

    private static double Positive(double value, string name)
    {
        Finite(value, name);
        if (value <= 0)
            throw new ArgumentException($"{name}必须为正数，且应处于可计算的工程量级范围。");
        return value;
    }

    private static double Finite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException($"{name}超出可计算的数值范围，请检查参数并使用工程量级数值。");
        return value;
    }

    private static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
