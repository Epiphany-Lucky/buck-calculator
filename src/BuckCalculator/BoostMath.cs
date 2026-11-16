using static BuckCalculator.BuckMath;

namespace BuckCalculator;

/// <summary>Ideal, single-phase, fixed-frequency CCM boost power-stage calculations.</summary>
public static class BoostMath
{
    public static BuckResult Calculate(BuckInputs p)
    {
        ArgumentNullException.ThrowIfNull(p);
        ValidateInputs(p, ConverterTopology.Boost);

        double frequency = Positive(p.FrequencyKhz * 1_000, "开关频率");
        double minimumRatio = Positive(p.VinMin / p.Vout, "最小电压比");
        double maximumRatio = Positive(p.VinMax / p.Vout, "最大电压比");
        double dutyMin = Positive(1 - maximumRatio, "最小占空比");
        double dutyMax = Positive(1 - minimumRatio, "最大占空比");
        if (dutyMax >= 1 || maximumRatio >= 1)
            throw new ArgumentException("输入与输出电压比超出浮点计算精度，请使用可计算的升压范围。");

        double inductanceFactor = 1 - p.InductorTolerancePercent / 100;
        double capacitanceFactor = 1 - p.CapacitanceDeratingPercent / 100;
        // Relative ripple is ΔIL / IL,avg at each Vin. Its maximum is at x = Vin/Vout = 2/3.
        double relativeRippleRatio = Math.Clamp(2.0 / 3, minimumRatio, maximumRatio);
        double relativeRippleShape = relativeRippleRatio * relativeRippleRatio * (1 - relativeRippleRatio);
        double requiredInductance = Positive(
            p.Vout / frequency / p.IoutMax / (p.RipplePercent / 100)
            * relativeRippleShape / inductanceFactor * 1e6, "所需标称电感");
        double selectedInductance = p.InductorUh > 0 ? p.InductorUh : RoundUpE12(requiredInductance);
        double minimumInductance = Positive(selectedInductance * inductanceFactor, "最小有效电感");
        double inductanceHenries = Positive(minimumInductance * 1e-6, "有效电感换算");
        double rippleScale = Positive(p.Vout / frequency / inductanceHenries, "电感纹波系数");
        double boundary = Positive(rippleScale / 2 * relativeRippleShape, "最坏 CCM 输出边界");
        if (p.IoutMax <= boundary)
            throw new ArgumentException("候选电感使额定负载在部分输入范围达到临界导通或进入 DCM，超出额定 CCM 模型。请增大电感或开关频率。");

        double rippleRatio = Math.Clamp(0.5, minimumRatio, maximumRatio);
        double ripple = Positive(rippleScale * rippleRatio * (1 - rippleRatio), "最大电感纹波");
        double maximumRelativeRipple = Positive(
            rippleScale * relativeRippleShape / p.IoutMax * 100, "最大相对电感纹波");

        // With full-load CCM over the entire interval, peak and all semiconductor RMS currents
        // decrease with x. The minimum valley can instead occur at an interior point.
        double averageAtMinimumInput = Positive(p.IoutMax / minimumRatio, "最大平均电感电流");
        double rippleAtMinimumInput = Positive(rippleScale * minimumRatio * dutyMax, "最低输入电压电感纹波");
        double peak = Positive(averageAtMinimumInput + rippleAtMinimumInput / 2, "最大电感峰值电流");
        double waveformValley = Positive(averageAtMinimumInput - rippleAtMinimumInput / 2, "波形电感谷值电流");
        double valleyRatio = MinimumValleyRatio(minimumRatio, maximumRatio, p.IoutMax, rippleScale);
        double valley = Positive(p.IoutMax / valleyRatio - rippleScale / 2 * valleyRatio * (1 - valleyRatio), "最小电感谷值电流");
        double ratedCurrentSquared = Positive(p.IoutMax * p.IoutMax, "输出电流平方");
        double meanSquare = Positive(averageAtMinimumInput * averageAtMinimumInput
            + rippleAtMinimumInput * rippleAtMinimumInput / 12, "电感均方电流");
        double inductorRms = Positive(Math.Sqrt(meanSquare), "电感 RMS 电流");
        double switchRms = Positive(Math.Sqrt(dutyMax * meanSquare), "主开关 RMS 电流");
        double rectifierRms = Positive(Math.Sqrt(minimumRatio * meanSquare), "整流支路 RMS 电流");
        // Expanded form avoids subtracting nearly equal squares at very small duty cycles.
        double outputCapacitorRms = Positive(Math.Sqrt(ratedCurrentSquared * dutyMax / minimumRatio
            + minimumRatio * rippleAtMinimumInput * rippleAtMinimumInput / 12), "输出电容 RMS 电流");

        double esrRipple = Finite(peak * p.EsrMilliohm, "ESR 纹波");
        double remainingRipple = Positive((p.OutputRippleMv - esrRipple) * 1e-3,
            "扣除 ESR 后的输出纹波预算，请减小 ESR 或调整纹波目标");
        // If IL,valley < Iout, the capacitor begins discharging before the off interval ends.
        // Integrate the positive triangular charging current instead of using only Iout × D / fs.
        double charge = Positive(OutputCharge(p.IoutMax, minimumRatio, dutyMax,
            peak, waveformValley, rippleAtMinimumInput, frequency), "输出电容峰峰值电荷");
        double requiredCapacitance = Positive(charge / remainingRipple / capacitanceFactor * 1e6, "所需标称输出电容");
        double selectedCapacitance = p.OutputCapacitorUf > 0 ? p.OutputCapacitorUf : RoundUpE12(requiredCapacitance);
        double effectiveCapacitance = Positive(selectedCapacitance * capacitanceFactor, "有效输出电容");
        double capacitanceFarads = Positive(effectiveCapacitance * 1e-6, "有效电容换算");
        double capacitiveRipple = Positive(charge / capacitanceFarads * 1e3, "电容充放电纹波");
        double totalRipple = Positive(capacitiveRipple + esrRipple, "总输出纹波");
        double inputCapacitance = Positive(ripple / (8 * frequency)
            / Positive(p.InputRippleMv * 1e-3, "输入纹波换算") * 1e6, "所需有效输入电容");
        double inputRms = Positive(ripple / Math.Sqrt(12), "输入电容 RMS 电流");
        double outputPower = Positive(p.Vout * p.IoutMax, "输出功率");
        double loadResistance = Positive(p.Vout / p.IoutMax, "额定等效负载");
        double rippleVin = rippleRatio * p.Vout;
        double boundaryVin = relativeRippleRatio * p.Vout;
        double valleyVin = valleyRatio * p.Vout;

        bool inductorTargetMet = maximumRelativeRipple <= p.RipplePercent * (1 + 1e-9);
        bool capacitorTargetMet = totalRipple <= p.OutputRippleMv * (1 + 1e-9);
        double modeTolerance = boundary * 1e-9;
        string operatingMode = p.IoutOperating > boundary + modeTolerance ? "CCM（全输入范围）"
            : Math.Abs(p.IoutOperating - boundary) <= modeTolerance ? $"临界导通（Vin = {Format(boundaryVin)} V）"
            : "DCM（部分或全部输入范围）";

        var notes = new List<string>
        {
            "计算采用理想单相 BOOST 的固定频率稳态 CCM 模型，D = 1 − Vin / Vout，平均电感电流为 Iout / (1 − D)。所有纹波与器件电流应力按额定最大输出负载计算。",
            $"电感纹波目标为各输入点的 ΔIL / IL,avg，最坏比例在 Vin = {Format(boundaryVin)} V。绝对电感纹波最坏点为 {Format(rippleVin)} V，两者分别检查全输入范围。",
            $"电感负容差为 {Format(p.InductorTolerancePercent)}%，输出电容有效容量折减为 {Format(p.CapacitanceDeratingPercent)}%。器件容差、直流偏压与温度需结合实际数据手册确定。",
            "输出电容充放电纹波由电容电流的分段电荷计算。当电感谷值低于输出负载电流时，包含关断区间内的额外放电。ESR 纹波按电感峰值 × ESR 估算，电容项与 ESR 项相加作为保守纹波预算。",
            "输入电容有效容量下限采用连续三角输入纹波模型 ΔIL / (8 × fs × ΔVin)，输入电容 RMS 为 ΔIL / sqrt(12)。输入 ESR、ESL、供电源阻抗与暂态储能需进一步核验。",
            "峰值、电感 RMS、主开关 RMS、整流支路 RMS、输出电容 RMS 与输出纹波均在最低输入电压处最坏。电感谷值最小点单独检查区间内部极值，波形按最低输入电压的同一额定工作点绘制。",
            "实际选型需核对电感饱和与热电流、主开关及整流器耐压和限流、最小导通/关断时间、环路补偿及 BOOST 右半平面零点。"
        };
        if (!inductorTargetMet)
            notes.Add($"候选电感的最坏相对纹波为当地平均电感电流的 {Format(maximumRelativeRipple)}%，超过目标 {Format(p.RipplePercent)}%。");
        if (!capacitorTargetMet)
            notes.Add($"候选输出电容的最坏输出纹波估算为 {Format(totalRipple)} mV，超过目标 {Format(p.OutputRippleMv)} mV。");
        if (operatingMode.StartsWith("DCM", StringComparison.Ordinal))
            notes.Add($"当前输出负载 {Format(p.IoutOperating)} A 低于最坏 CCM 输出边界 {Format(boundary)} A。二极管或二极管仿真控制下会在部分或全部输入范围进入 DCM，该负载的实际占空比应采用 DCM 模型。波形与应力表按额定最大负载的 CCM 工作点显示。");
        else if (operatingMode.StartsWith("临界", StringComparison.Ordinal))
            notes.Add($"当前输出负载在 Vin = {Format(boundaryVin)} V 达到 CCM/DCM 边界。波形与应力表按额定最大负载计算。");
        else
            notes.Add($"当前输出负载 {Format(p.IoutOperating)} A 大于最坏 CCM 输出边界 {Format(boundary)} A，理想模型下全输入范围处于 CCM。波形与应力表按额定最大负载计算。");

        var metrics = new List<ResultMetric>
        {
            new("工作点", "最小占空比", Format(dutyMin * 100), "%", $"Vin = {Format(p.VinMax)} V，理想 CCM"),
            new("工作点", "最大占空比", Format(dutyMax * 100), "%", $"Vin = {Format(p.VinMin)} V，理想 CCM"),
            new("工作点", "额定输出功率", Format(outputPower), "W", "按最大输出电流计算"),
            new("工作点", "额定等效负载", Format(loadResistance), "Ω", "Vout / Iout,max"),
            new("电感", "所需标称电感下限", Format(requiredInductance), "µH", $"全范围约束当地平均 IL 的纹波比例，最坏 Vin = {Format(boundaryVin)} V，包含负容差"),
            new("电感", "采用标称电感", Format(selectedInductance), "µH", p.InductorUh > 0 ? "使用手动候选值" : "E12 标称系列向上取值"),
            new("电感", "最小有效电感", Format(minimumInductance), "µH", "标称值 × (1 − 负容差比例)"),
            new("电感", "最坏电感纹波", Format(ripple), "A p-p", $"Vin = {Format(rippleVin)} V"),
            new("电感", "最坏相对电感纹波", Format(maximumRelativeRipple), "%", $"ΔIL / IL,avg，Vin = {Format(boundaryVin)} V"),
            new("电感", "最大平均电感电流", Format(averageAtMinimumInput), "A", $"Vin = {Format(p.VinMin)} V，Iout,max / (1 − D)"),
            new("电感", "电感峰值电流", Format(peak), "A", $"Vin = {Format(p.VinMin)} V，额定最大负载"),
            new("电感", "电感谷值最小值", Format(valley), "A", $"Vin = {Format(valleyVin)} V，检查区间内部极值"),
            new("电感", "电感 RMS 电流", Format(inductorRms), "A RMS", $"Vin = {Format(p.VinMin)} V，包含三角纹波"),
            new("电感", "最坏 CCM 输出边界", Format(boundary), "A", $"(1 − D) × ΔIL / 2，Vin = {Format(boundaryVin)} V"),
            new("电感", "工作负载导通模式", operatingMode, "", "按输出负载与全范围 CCM 边界判断"),
            new("输出电容", "所需标称输出电容下限", Format(requiredCapacitance), "µF", "分段电容电荷，扣除 ESR 纹波预算，包含有效容量折减"),
            new("输出电容", "采用标称输出电容", Format(selectedCapacitance), "µF", p.OutputCapacitorUf > 0 ? "使用手动候选值" : "E12 标称系列向上取值"),
            new("输出电容", "有效输出电容", Format(effectiveCapacitance), "µF", "标称容量 × (1 − 折减比例)"),
            new("输出电容", "输出电容 RMS 电流", Format(outputCapacitorRms), "A RMS", $"Vin = {Format(p.VinMin)} V，包含三角纹波"),
            new("输出电容", "电容充放电纹波", Format(capacitiveRipple), "mV p-p", $"分段电荷 Qpp / Cout,eff，Vin = {Format(p.VinMin)} V"),
            new("输出电容", "ESR 纹波", Format(esrRipple), "mV p-p", "IL,peak × ESR"),
            new("输出电容", "总输出纹波估算", Format(totalRipple), "mV p-p", $"充放电项与 ESR 项相加，Vin = {Format(p.VinMin)} V"),
            new("输入与开关", "有效输入电容下限", Format(inputCapacitance), "µF", $"ΔIL / (8 × fs × ΔVin)，Vin = {Format(rippleVin)} V，实际选型需考虑降额与输入 ESR"),
            new("输入与开关", "输入电容 RMS 电流", Format(inputRms), "A RMS", $"ΔIL / sqrt(12)，Vin = {Format(rippleVin)} V"),
            new("输入与开关", "主开关 RMS 电流", Format(switchRms), "A RMS", $"低侧主开关，Vin = {Format(p.VinMin)} V"),
            new("输入与开关", "整流支路 RMS 电流", Format(rectifierRms), "A RMS", $"二极管或同步整流器，Vin = {Format(p.VinMin)} V"),
            new("输入与开关", "二极管平均电流", Format(p.IoutMax), "A", "理想稳态等于额定输出电流"),
            new("输入与开关", "主开关理想关断电压", Format(p.Vout), "V", "等于输出电压，器件耐压需另计寄生尖峰与设计裕量"),
            new("输入与开关", "二极管理想反向电压", Format(p.Vout), "V", "等于输出电压，器件反向耐压需另计寄生尖峰与设计裕量")
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
            MainSwitchRms = switchRms,
            RectifierRms = rectifierRms,
            OutputCapacitorRms = outputCapacitorRms,
            RippleVin = rippleVin,
            PeakCurrentVin = p.VinMin,
            ValleyCurrentVin = valleyVin,
            BoundaryVin = boundaryVin,
            MaximumRelativeRipplePercent = maximumRelativeRipple,
            WaveformVin = p.VinMin,
            WaveformDuty = dutyMax,
            WaveformAverageCurrent = averageAtMinimumInput,
            WaveformRipple = rippleAtMinimumInput,
            WaveformPeakCurrent = peak,
            WaveformValleyCurrent = waveformValley,
            OutputPower = outputPower,
            LoadResistance = loadResistance,
            Metrics = metrics,
            Notes = notes
        };
    }

    private static double MinimumValleyRatio(double minimum, double maximum, double current, double rippleScale)
    {
        if (current >= rippleScale / 2)
            return maximum;
        double target = current / (rippleScale / 2);
        double lower = 0.5;
        double upper = 1;
        // x²(2x−1) is strictly increasing on [1/2, 1]. Its root gives the unique valley minimum.
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double middle = (lower + upper) / 2;
            if (middle * middle * (2 * middle - 1) < target)
                lower = middle;
            else
                upper = middle;
        }
        return Math.Clamp((lower + upper) / 2, minimum, maximum);
    }

    private static double OutputCharge(double current, double ratio, double duty,
        double peak, double valley, double ripple, double frequency)
    {
        if (valley >= current)
            return current * duty / frequency;
        double chargingPeak = peak - current;
        return ratio * chargingPeak / frequency * (chargingPeak / ripple) / 2;
    }
}
