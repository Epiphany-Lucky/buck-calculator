using System.Globalization;
using System.Text;

namespace BuckCalculator;

public static class ReportExporter
{
    private static readonly CultureInfo NumberCulture = CultureInfo.InvariantCulture;

    public static string ToMarkdown(BuckResult result)
    {
        var report = new StringBuilder();
        report.AppendLine($"# {TopologyName(result.Inputs.Topology)} 电路计算报告");
        report.AppendLine();
        report.AppendLine($"生成时间为 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}。");
        report.AppendLine();
        report.AppendLine("计算采用固定开关频率、理想 CCM 稳态模型。额定满载结果计入电感负容差、输出电容折减和 ESR 纹波预算。器件选型应核对控制器数据手册、饱和电流、温升、耐压及直流偏置曲线。");
        report.AppendLine();
        report.AppendLine("## 输入参数");
        report.AppendLine();
        report.AppendLine("| 参数 | 数值 | 单位 |");
        report.AppendLine("|---|---:|---|");
        foreach (var input in InputRows(result.Inputs))
            report.AppendLine($"| {input.Label} | {input.Value} | {input.Unit} |");
        report.AppendLine();
        report.AppendLine("## 计算结果");
        report.AppendLine();
        report.AppendLine("| 分组 | 参数 | 数值 | 单位 | 说明 |");
        report.AppendLine("|---|---|---:|---|---|");
        foreach (var metric in result.Metrics)
            report.AppendLine($"| {metric.Section} | {metric.Label} | {metric.Value} | {metric.Unit} | {metric.Detail} |");
        report.AppendLine();
        report.AppendLine("## 工程检查");
        report.AppendLine();
        foreach (string note in result.Notes)
            report.AppendLine($"- {note}");
        report.AppendLine();
        report.AppendLine("## 计算关系");
        report.AppendLine();
        report.AppendLine("以下关系使用 SI 单位。ΔIL 为电感电流峰峰值。");
        report.AppendLine();
        report.AppendLine("```text");
        report.AppendLine(GetFormulaText(result.Inputs.Topology));
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("输入电容容量采用有效容量初估，输入端 ESR、供电源阻抗及暂态储能应进一步核验。RMS 电流在输入电压区间内求极值。输出纹波取电容项与 ESR 项之和作为保守预算。");
        report.AppendLine();
        report.AppendLine("## 参考资料");
        report.AppendLine();
        if (result.Inputs.Topology == ConverterTopology.Boost)
        {
            report.AppendLine("- [Texas Instruments — Basic Calculation of a Boost Converter’s Power Stage](https://www.ti.com/lit/an/slva372d/slva372d.pdf)");
            report.AppendLine("- [Analog Devices — LTC1871 Datasheet, Boost Converter Applications](https://www.analog.com/media/en/technical-documentation/data-sheets/1871fe.pdf)");
            report.AppendLine();
            report.AppendLine("本报告采用理想效率模型。BOOST 输出电容在谷值低于负载电流时的 Qpp 分段式由理想 CCM 电容电流积分得到，区间极值由相应函数求导确定。");
        }
        else
        {
            report.AppendLine("- [Analog Devices — Buck Power Stage Design Equations](https://www.analog.com/en/resources/app-notes/2022/11/22/06/53/buck-power-stage-design-equations.html)");
            report.AppendLine("- [Texas Instruments — Input and Output Capacitor Selection](https://www.ti.com/lit/an/slta055/slta055.pdf)");
        }
        return report.ToString();
    }

    public static string ToCsv(BuckResult result)
    {
        var report = new StringBuilder();
        report.AppendLine("\"分组\",\"参数\",\"数值\",\"单位\",\"说明\"");
        foreach (var input in InputRows(result.Inputs))
            AppendCsvRow(report, "输入", input.Label, input.Value, input.Unit, "本轮计算参数");
        foreach (var metric in result.Metrics)
            AppendCsvRow(report, metric.Section, metric.Label, metric.Value, metric.Unit, metric.Detail);
        foreach (string note in result.Notes)
            AppendCsvRow(report, "工程检查", "说明", "", "", note);
        AppendCsvRow(report, "模型", "适用条件", "理想 CCM", "", "固定频率稳态，输入电容容量为初估，具体器件及暂态应进一步核验");
        return report.ToString();
    }

    private static IEnumerable<(string Label, string Value, string Unit)> InputRows(BuckInputs input)
    {
        yield return ("计算拓扑", TopologyName(input.Topology), "");
        yield return ("输入电压下限", Format(input.VinMin), "V");
        yield return ("输入电压上限", Format(input.VinMax), "V");
        yield return ("输出电压", Format(input.Vout), "V");
        yield return ("最大输出电流", Format(input.IoutMax), "A");
        yield return ("检查工作负载", Format(input.IoutOperating), "A");
        yield return ("开关频率", Format(input.FrequencyKhz), "kHz");
        yield return (input.Topology == ConverterTopology.Boost
            ? "电感纹波目标（相对当地平均 IL）" : "电感纹波目标（相对额定输出电流）", Format(input.RipplePercent), "%");
        yield return ("输出纹波预算", Format(input.OutputRippleMv), "mVpp");
        yield return ("输入纹波预算", Format(input.InputRippleMv), "mVpp");
        yield return ("电感负容差", Format(input.InductorTolerancePercent), "%");
        yield return ("输出电容容量折减", Format(input.CapacitanceDeratingPercent), "%");
        yield return ("输出电容 ESR", Format(input.EsrMilliohm), "mΩ");
        yield return ("候选标称电感", input.InductorUh == 0 ? "自动选取 E12" : Format(input.InductorUh), "µH");
        yield return ("候选标称输出电容", input.OutputCapacitorUf == 0 ? "自动选取 E12" : Format(input.OutputCapacitorUf), "µF");
    }

    public static string GetFormulaText(ConverterTopology topology) => topology switch
    {
        ConverterTopology.Buck => """
            D = Vout / Vin
            Lmin = Lnom × (1 − 电感负容差)
            ΔIL = Vout × (1 − D) / (Lmin × fs)
            IL,avg = Iout,max
            IL,peak = IL,avg + ΔIL/2
            IL,valley = IL,avg − ΔIL/2
            IL,rms = sqrt(IL,avg² + ΔIL²/12)
            Iout,boundary = ΔIL/2
            Ceff = Cnom × (1 − 容量折减)
            ΔVout ≤ ΔIL/(8 × fs × Ceff) + ΔIL × ESR
            Icout,rms = ΔIL / sqrt(12)
            Cin,eff ≥ Iout,max × D × (1−D) / (fs × ΔVin)  [忽略电感纹波的初估]
            Icin,rms² = Iout,max² × D × (1−D) + D × ΔIL(D)²/12
            Imain,rms² = D × (IL,avg² + ΔIL(D)²/12)
            Irectifier,rms² = (1−D) × (IL,avg² + ΔIL(D)²/12)
            """,
        ConverterTopology.Boost => """
            D = 1 − Vin / Vout
            x = 1 − D = Vin / Vout
            Lmin = Lnom × (1 − 电感负容差)
            IL,avg = Iout,max / x
            ΔIL = Vin × D / (Lmin × fs)
            纹波目标 = ΔIL / IL,avg，按全输入范围约束
            IL,peak = IL,avg + ΔIL/2
            IL,valley = IL,avg − ΔIL/2
            IL,rms = sqrt(IL,avg² + ΔIL²/12)
            Iout,boundary = x × ΔIL/2
            Ceff = Cnom × (1 − 容量折减)
            IL,valley ≥ Iout,max 时，Qpp = Iout,max × D / fs
            IL,valley < Iout,max 时，Qpp = x × (IL,peak − Iout,max)² / (2 × ΔIL × fs)
            ΔVout ≤ Qpp / Ceff + IL,peak × ESR
            Icout,rms² = Iout,max² × D / x + x × ΔIL²/12
            Cin,eff ≥ ΔIL / (8 × fs × ΔVin)  [连续三角纹波初估]
            Icin,rms = ΔIL / sqrt(12)
            Imain,rms² = D × (IL,avg² + ΔIL²/12)
            Irectifier,rms² = x × (IL,avg² + ΔIL²/12)
            绝对纹波最坏 Vin = clamp(Vout/2, Vin,min, Vin,max)
            相对纹波与 CCM 输出边界最坏 Vin = clamp(2Vout/3, Vin,min, Vin,max)
            额定全范围 CCM 时，峰值、电感/主开关/整流支路/输出电容 RMS 最坏 Vin = Vin,min
            输出电容电荷与 ESR 纹波最坏 Vin = Vin,min
            谷值最小点由 x²(2x−1) = 2Iout,max × Lmin × fs / Vout 求根并限制在输入范围
            """,
        _ => throw new ArgumentException("请选择有效的 BUCK 或 BOOST 拓扑。", nameof(topology))
    };

    private static string TopologyName(ConverterTopology topology) => topology switch
    {
        ConverterTopology.Buck => "BUCK",
        ConverterTopology.Boost => "BOOST",
        _ => throw new ArgumentException("请选择有效的 BUCK 或 BOOST 拓扑。", nameof(topology))
    };

    private static string Format(double value) => value.ToString("G10", NumberCulture);

    private static void AppendCsvRow(StringBuilder report, params string[] cells)
    {
        report.AppendLine(string.Join(",", cells.Select(cell => "\"" + cell.Replace("\"", "\"\"") + "\"")));
    }
}
