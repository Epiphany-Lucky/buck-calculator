using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace BuckCalculator;

public sealed class MainWindow : Window
{
    private static readonly Brush Ink = ColorBrush("#1D1D1F");
    private static readonly Brush Muted = ColorBrush("#86868B");
    private static readonly Brush Accent = ColorBrush("#007AFF");
    private static readonly Brush Line = ColorBrush("#E5E5EA");
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Button calculateButton = new() { Content = "计算并检查", Height = 42 };
    private readonly Button exportButton = new() { Content = "导出报告", MinWidth = 104, Height = 36 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19 };
    private readonly TextBlock modeStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock waveformDescription = new() { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel metricPanel = new();
    private readonly StackPanel notePanel = new();
    private readonly Canvas waveform = new() { Height = 184, ClipToBounds = true };
    private readonly ScrollViewer resultScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Dictionary<string, TextBlock> summaryValues = [];
    private readonly Dictionary<string, TextBlock> summaryDetails = [];
    private readonly Button firstExampleButton = new() { Content = "5 V / 2 A", Width = 99, Height = 30, ToolTip = "载入 9–16 V 输入、5 V / 2 A 示例" };
    private readonly Button secondExampleButton = new() { Content = "12 V / 3 A", Width = 104, Height = 30, ToolTip = "载入 18–30 V 输入、12 V / 3 A 示例" };
    private bool updatingInputs;

    public BuckResult? CurrentResult { get; private set; }

    public MainWindow()
    {
        Title = "BUCK 计算器 · Epiphany";
        Width = 1360;
        Height = 920;
        MinWidth = 1000;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ColorBrush("#F5F5F7");
        Foreground = Ink;
        FontFamily = new FontFamily("Inter, Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResources();

        var layout = new Grid { Background = Background };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(BuildHeader());

        var workspace = new Grid { Margin = new Thickness(24, 0, 24, 20) };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(318) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(workspace, 1);
        layout.Children.Add(workspace);

        workspace.Children.Add(BuildInputs());
        var results = BuildResults();
        Grid.SetColumn(results, 2);
        workspace.Children.Add(results);
        Content = layout;

        calculateButton.Click += (_, _) => Calculate();
        exportButton.Click += (_, _) => ExportReport();
        waveform.SizeChanged += (_, _) => DrawWaveform();
        firstExampleButton.Click += (_, _) => ApplyExample(0);
        secondExampleButton.Click += (_, _) => ApplyExample(1);
        ApplyExample(0);
        Loaded += (_, _) => DrawWaveform();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => ExportReport(), (_, e) => e.CanExecute = CurrentResult is not null));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
    }

    private void SetResources()
    {
        var textBoxStyle = new Style(typeof(TextBox));
        textBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        textBoxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        textBoxStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        textBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        textBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        textBoxStyle.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        textBoxStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var inputTemplate = new ControlTemplate(typeof(TextBox));
        var inputBorder = new FrameworkElementFactory(typeof(Border));
        inputBorder.Name = "InputBorder";
        inputBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        inputBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        inputBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        inputBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var inputHost = new FrameworkElementFactory(typeof(ScrollViewer));
        inputHost.Name = "PART_ContentHost";
        inputHost.SetValue(Control.FocusableProperty, false);
        inputBorder.AppendChild(inputHost);
        inputTemplate.VisualTree = inputBorder;
        textBoxStyle.Setters.Add(new Setter(Control.TemplateProperty, inputTemplate));
        var inputFocus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        inputFocus.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
        textBoxStyle.Triggers.Add(inputFocus);
        Resources.Add(typeof(TextBox), textBoxStyle);

        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        buttonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 5, 14, 5)));
        buttonStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        buttonStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentFactory.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        borderFactory.AppendChild(contentFactory);
        buttonTemplate.VisualTree = borderFactory;
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.84));
        buttonStyle.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        buttonStyle.Triggers.Add(disabled);
        Resources.Add(typeof(Button), buttonStyle);
    }

    private UIElement BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(27, 22, 27, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titlePanel = new StackPanel();
        titlePanel.Children.Add(new TextBlock { Text = "BUCK 计算器", FontSize = 27, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        titlePanel.Children.Add(new TextBlock { Text = "降压电源参数设计与最坏工况检查", FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 5, 0, 0) });
        header.Children.Add(titlePanel);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(new TextBlock { Text = "示例", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        firstExampleButton.BorderThickness = new Thickness(0);
        secondExampleButton.BorderThickness = new Thickness(0);
        firstExampleButton.FontSize = 12;
        secondExampleButton.FontSize = 12;
        segments.Children.Add(firstExampleButton);
        segments.Children.Add(secondExampleButton);
        tools.Children.Add(new Border { Child = segments, Background = ColorBrush("#E8E8ED"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Margin = new Thickness(0, 0, 12, 0) });
        tools.Children.Add(exportButton);
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);
        return header;
    }

    private UIElement BuildInputs()
    {
        var panel = new Grid { Margin = new Thickness(18) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = "设计参数", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });

        var inputFields = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        inputFields.Children.Add(SectionLabel("电源规格"));
        inputFields.Children.Add(FieldPair(("VinMin", "输入下限", "V"), ("VinMax", "输入上限", "V")));
        inputFields.Children.Add(FieldPair(("Vout", "输出电压", "V"), ("IoutMax", "额定输出电流", "A")));
        inputFields.Children.Add(FieldPair(("FrequencyKhz", "开关频率", "kHz"), ("IoutOperating", "检查工作负载", "A")));
        inputFields.Children.Add(SectionLabel("纹波与容差"));
        inputFields.Children.Add(FieldPair(("RipplePercent", "电感纹波目标", "%"), ("OutputRippleMv", "输出纹波预算", "mVpp")));
        inputFields.Children.Add(FieldPair(("InductorTolerancePercent", "电感负容差", "%"), ("CapacitanceDeratingPercent", "输出容量折减", "%")));
        inputFields.Children.Add(FieldPair(("EsrMilliohm", "输出电容 ESR", "mΩ"), ("InputRippleMv", "输入纹波预算", "mVpp")));
        inputFields.Children.Add(SectionLabel("候选元件"));
        inputFields.Children.Add(FieldPair(("InductorUh", "标称电感", "µH"), ("OutputCapacitorUf", "标称输出电容", "µF")));
        inputFields.Children.Add(new TextBlock
        {
            Text = "候选值填 0 时自动向上选择 E12 档位。容量折减用于合并考虑容差、直流偏置与温度影响。",
            Foreground = Muted,
            FontSize = 11.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var scroll = new ScrollViewer
        {
            Content = inputFields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        calculateButton.Background = Accent;
        calculateButton.Foreground = Brushes.White;
        calculateButton.BorderBrush = Accent;
        calculateButton.IsDefault = true;
        footer.Children.Add(calculateButton);
        status.Margin = new Thickness(0, 10, 0, 0);
        footer.Children.Add(status);
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);
        return Card(panel, 0);
    }

    private UIElement BuildResults()
    {
        var results = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var summary = new UniformGrid { Columns = 2, Rows = 2, Margin = new Thickness(-5, -5, -5, 11) };
        summary.Children.Add(SummaryCard("inductor", "标称电感", "µH"));
        summary.Children.Add(SummaryCard("capacitor", "标称输出电容", "µF"));
        summary.Children.Add(SummaryCard("peak", "电感峰值电流", "A"));
        summary.Children.Add(SummaryCard("ripple", "输出纹波估算", "mVpp"));
        results.Children.Add(summary);

        var modeContent = new StackPanel { Margin = new Thickness(14, 11, 14, 11) };
        modeContent.Children.Add(modeStatus);
        var modeBorder = Card(modeContent, 0);
        modeBorder.Background = ColorBrush("#EDF5FF");
        modeBorder.BorderBrush = ColorBrush("#DCEAFF");
        modeBorder.Margin = new Thickness(0, 0, 0, 14);
        results.Children.Add(modeBorder);

        var plotPanel = new StackPanel();
        plotPanel.Children.Add(new TextBlock { Text = "电感电流 · 额定负载", FontSize = 16, FontWeight = FontWeights.SemiBold });
        waveformDescription.Margin = new Thickness(0, 6, 0, 4);
        plotPanel.Children.Add(waveformDescription);
        plotPanel.Children.Add(waveform);
        var plotCard = Card(plotPanel, 18);
        plotCard.Margin = new Thickness(0, 0, 0, 14);
        results.Children.Add(plotCard);

        var metricsContent = new StackPanel();
        metricsContent.Children.Add(new TextBlock { Text = "计算明细", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        metricsContent.Children.Add(metricPanel);
        var metricCard = Card(metricsContent, 18);
        metricCard.Margin = new Thickness(0, 0, 0, 14);
        results.Children.Add(metricCard);

        var notesContent = new StackPanel();
        notesContent.Children.Add(new TextBlock { Text = "工程检查", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        notesContent.Children.Add(notePanel);
        var notesCard = Card(notesContent, 18);
        notesCard.Margin = new Thickness(0, 0, 0, 14);
        results.Children.Add(notesCard);

        var formulas = new Expander { Header = "计算关系与适用条件", Foreground = Muted, FontSize = 13, Padding = new Thickness(2) };
        formulas.Content = new TextBlock
        {
            Text = "固定频率、理想 CCM 稳态模型。ΔIL 为电感电流峰峰值。\n\n"
                + "D = Vout / Vin\nLmin = Lnom × (1 − 负容差)\nΔIL = Vout × (1 − D) / (Lmin × fs)\n"
                + "IL,peak = Iout,max + ΔIL / 2\nIL,rms = √(Iout,max² + ΔIL² / 12)\n"
                + "Ceff = Cnom × (1 − 容量折减)\nΔVout ≤ ΔIL / (8 × fs × Ceff) + ΔIL × ESR\n\n"
                + "采用 SI 单位计算。输出纹波为电容项与 ESR 项之和的保守预算。输入电容容量为忽略电感纹波的一阶估算，RMS 电流在输入区间内求最坏值。\n\n"
                + "元件选型需核对控制器占空比与最小导通时间、开关损耗、器件耐压、电感饱和和温升、电容偏置曲线及负载暂态。",
            FontSize = 12,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 8)
        };
        results.Children.Add(formulas);
        results.Children.Add(new TextBlock { Text = "BUCK Calculator 1.0.0  ·  Epiphany", FontSize = 11, Foreground = Muted, Margin = new Thickness(2, 14, 0, 4) });
        resultScroll.Content = results;
        resultScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        return resultScroll;
    }

    private UIElement FieldPair((string Key, string Label, string Unit) left, (string Key, string Label, string Unit) right)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(InputField(left.Key, left.Label, left.Unit));
        var rightField = InputField(right.Key, right.Label, right.Unit);
        Grid.SetColumn(rightField, 2);
        grid.Children.Add(rightField);
        return grid;
    }

    private UIElement InputField(string key, string label, string unit)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 5) });
        var entry = new Grid();
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { MinWidth = 50, Height = 36, ToolTip = $"{label}，单位 {unit}" };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => MarkInputsChanged();
        fields.Add(key, box);
        entry.Children.Add(box);
        var unitLabel = new TextBlock { Text = unit, FontSize = 10.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        Grid.SetColumn(unitLabel, 1);
        entry.Children.Add(unitLabel);
        panel.Children.Add(entry);
        return panel;
    }

    private UIElement SummaryCard(string key, string label, string unit)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Muted });
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
        var value = new TextBlock { Text = "—", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Ink };
        summaryValues.Add(key, value);
        valueRow.Children.Add(value);
        valueRow.Children.Add(new TextBlock { Text = unit, FontSize = 13, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7, 0, 0, 4) });
        stack.Children.Add(valueRow);
        var detail = new TextBlock { Text = "等待计算", FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        summaryDetails.Add(key, detail);
        stack.Children.Add(detail);
        var card = Card(stack, 17);
        card.Margin = new Thickness(5);
        return card;
    }

    private void MarkInputsChanged()
    {
        if (updatingInputs)
            return;
        CurrentResult = null;
        exportButton.IsEnabled = false;
        status.Text = "参数已更改，请重新计算。";
        status.Foreground = Muted;
        modeStatus.Text = "等待重新计算";
        foreach (var value in summaryValues.Values)
            value.Text = "—";
        foreach (var detail in summaryDetails.Values)
            detail.Text = "参数已更改";
        metricPanel.Children.Clear();
        notePanel.Children.Clear();
        waveform.Children.Clear();
        waveformDescription.Text = "计算后显示最高输入电压、电感下限下的稳态波形。";
    }

    private void ApplyExample(int index)
    {
        firstExampleButton.Background = index == 0 ? Brushes.White : Brushes.Transparent;
        firstExampleButton.Foreground = index == 0 ? Ink : Muted;
        secondExampleButton.Background = index == 1 ? Brushes.White : Brushes.Transparent;
        secondExampleButton.Foreground = index == 1 ? Ink : Muted;
        var input = index == 1
            ? new BuckInputs { VinMin = 18, VinMax = 30, Vout = 12, IoutMax = 3, IoutOperating = 3, FrequencyKhz = 300, OutputRippleMv = 30 }
            : new BuckInputs();
        SetInputs(input);
        Calculate();
        resultScroll.ScrollToTop();
    }

    private void SetInputs(BuckInputs input)
    {
        updatingInputs = true;
        foreach (var property in typeof(BuckInputs).GetProperties())
        {
            if (fields.TryGetValue(property.Name, out var field))
                field.Text = Convert.ToDouble(property.GetValue(input), CultureInfo.InvariantCulture).ToString("G10", CultureInfo.InvariantCulture);
        }
        updatingInputs = false;
        MarkInputsChanged();
    }

    private double Read(string key)
    {
        var field = fields[key];
        if (!double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            field.Focus();
            field.SelectAll();
            string label = System.Windows.Automation.AutomationProperties.GetName(field);
            throw new ArgumentException($"请为“{label}”输入有限数字，可使用小数或科学计数法。");
        }
        return value;
    }

    private BuckInputs ReadInputs() => new()
    {
        VinMin = Read("VinMin"),
        VinMax = Read("VinMax"),
        Vout = Read("Vout"),
        IoutMax = Read("IoutMax"),
        IoutOperating = Read("IoutOperating"),
        FrequencyKhz = Read("FrequencyKhz"),
        RipplePercent = Read("RipplePercent"),
        OutputRippleMv = Read("OutputRippleMv"),
        InductorTolerancePercent = Read("InductorTolerancePercent"),
        CapacitanceDeratingPercent = Read("CapacitanceDeratingPercent"),
        EsrMilliohm = Read("EsrMilliohm"),
        InductorUh = Read("InductorUh"),
        OutputCapacitorUf = Read("OutputCapacitorUf"),
        InputRippleMv = Read("InputRippleMv")
    };

    private void Calculate()
    {
        try
        {
            var result = BuckMath.Calculate(ReadInputs());
            CurrentResult = result;
            exportButton.IsEnabled = true;
            ShowResult(result);
            status.Text = result.MeetsRippleTargets ? "计算完成，纹波预算检查通过。" : "计算完成，请检查纹波与工作模式提示。";
            status.Foreground = result.MeetsRippleTargets ? Accent : ColorBrush("#A56013");
        }
        catch (ArgumentException exception)
        {
            CurrentResult = null;
            exportButton.IsEnabled = false;
            status.Text = exception.Message;
            status.Foreground = ColorBrush("#B23E36");
        }
    }

    private void ShowResult(BuckResult result)
    {
        summaryValues["inductor"].Text = Number(result.SelectedInductanceUh);
        summaryDetails["inductor"].Text = $"容差下限 {Number(result.MinimumInductanceUh)} µH";
        summaryValues["capacitor"].Text = Number(result.SelectedOutputCapacitanceUf);
        summaryDetails["capacitor"].Text = $"有效容量 {Number(result.EffectiveOutputCapacitanceUf)} µF";
        summaryValues["peak"].Text = Number(result.PeakCurrent);
        summaryDetails["peak"].Text = $"电感 RMS {Number(result.InductorRms)} A";
        summaryValues["ripple"].Text = Number(result.TotalOutputRippleMv);
        summaryDetails["ripple"].Text = $"纹波预算 {Number(result.Inputs.OutputRippleMv)} mVpp";
        summaryValues["ripple"].Foreground = result.TotalOutputRippleMv <= result.Inputs.OutputRippleMv ? Accent : ColorBrush("#A56013");
        modeStatus.Text = $"工作负载 {Number(result.Inputs.IoutOperating)} A · {result.OperatingMode}    |    最坏工况临界电流 {Number(result.BoundaryCurrent)} A";
        modeStatus.Foreground = result.OperatingMode.StartsWith("CCM", StringComparison.Ordinal) ? Accent : ColorBrush("#A56013");
        waveformDescription.Text = $"Vin = {Number(result.Inputs.VinMax)} V  ·  Lmin = {Number(result.MinimumInductanceUh)} µH  ·  ΔIL = {Number(result.CurrentRipple)} App  ·  周期 {Number(1000 / result.Inputs.FrequencyKhz)} µs";
        metricPanel.Children.Clear();
        string section = "";
        foreach (var metric in result.Metrics)
        {
            if (metric.Section != section)
            {
                section = metric.Section;
                metricPanel.Children.Add(new TextBlock { Text = section, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 10, 0, 5) });
            }
            metricPanel.Children.Add(MetricRow(metric));
        }
        notePanel.Children.Clear();
        foreach (string note in result.Notes)
            notePanel.Children.Add(new TextBlock { Text = "•  " + note, Foreground = Muted, FontSize = 12, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) });
        DrawWaveform();
    }

    private UIElement MetricRow(ResultMetric metric)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = metric.Label, TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var value = new TextBlock { Text = metric.Value + " " + metric.Unit, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        var detail = new TextBlock { Text = metric.Detail, Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, LineHeight = 18 };
        Grid.SetColumn(detail, 2);
        row.Children.Add(detail);
        return new Border { Child = row, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private void DrawWaveform()
    {
        waveform.Children.Clear();
        var result = CurrentResult;
        double width = waveform.ActualWidth;
        if (result is null || width < 120)
            return;
        if (!result.FullLoadCcm)
        {
            AddPlotText("额定负载已进入临界或断续区，请增大电感后检查 CCM 波形。", 12, 60, ColorBrush("#A56013"));
            return;
        }

        const double left = 62;
        const double top = 18;
        const double plotHeight = 125;
        double plotWidth = Math.Max(10, width - left - 25);
        double maxCurrent = result.PeakCurrent * 1.12;
        double minCurrent = Math.Max(0, result.ValleyCurrent - result.CurrentRipple * 0.38);
        double span = Math.Max(result.CurrentRipple * 1.7, maxCurrent - minCurrent);
        maxCurrent = minCurrent + span;
        double Y(double current) => top + plotHeight * (maxCurrent - current) / span;

        double duty = result.Inputs.Vout / result.Inputs.VinMax;
        double periodWidth = plotWidth / 2;
        var onRegion = new Rectangle { Width = periodWidth * duty, Height = plotHeight, Fill = ColorBrush("#F0F6FF") };
        Canvas.SetLeft(onRegion, left);
        Canvas.SetTop(onRegion, top);
        waveform.Children.Add(onRegion);
        foreach (double current in new[] { result.ValleyCurrent, result.Inputs.IoutMax, result.PeakCurrent })
        {
            AddLine(left, Y(current), left + plotWidth, Y(current), Line, 1, current == result.Inputs.IoutMax);
            AddPlotText(Number(current) + " A", 0, Y(current) - 8, Muted);
        }
        AddLine(left, top + plotHeight, left + plotWidth, top + plotHeight, Line, 1);
        var trace = new Polyline { Stroke = Accent, StrokeThickness = 2.8, StrokeLineJoin = PenLineJoin.Round };
        for (int cycle = 0; cycle < 2; cycle++)
        {
            double start = left + periodWidth * cycle;
            trace.Points.Add(new Point(start, Y(result.ValleyCurrent)));
            trace.Points.Add(new Point(start + periodWidth * duty, Y(result.PeakCurrent)));
            trace.Points.Add(new Point(start + periodWidth, Y(result.ValleyCurrent)));
        }
        waveform.Children.Add(trace);
        AddPlotText("0", left - 3, top + plotHeight + 9, Muted);
        AddPlotText("Ts", left + periodWidth - 7, top + plotHeight + 9, Muted);
        AddPlotText("2Ts", left + plotWidth - 18, top + plotHeight + 9, Muted);
        AddPlotText("导通", left + periodWidth * duty / 2 - 12, top - 2, Accent);
        AddPlotText($"D = {Number(duty * 100)}%", left + plotWidth - 92, top - 2, Muted);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness, bool dashed = false)
    {
        waveform.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush, StrokeThickness = thickness,
            StrokeDashArray = dashed ? new DoubleCollection([4, 4]) : null
        });
    }

    private void AddPlotText(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = text, FontSize = 10.5, Foreground = brush, MaxWidth = Math.Max(100, waveform.ActualWidth - x - 8), TextWrapping = TextWrapping.Wrap };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        waveform.Children.Add(label);
    }

    private void ExportReport()
    {
        if (CurrentResult is null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出本轮 BUCK 计算报告",
            FileName = $"BUCK-{Number(CurrentResult.Inputs.Vout)}V-{Number(CurrentResult.Inputs.IoutMax)}A-计算报告",
            Filter = "Markdown 报告 (*.md)|*.md|CSV 表格 (*.csv)|*.csv",
            DefaultExt = ".md",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            string content = dialog.FilterIndex == 2 ? ReportExporter.ToCsv(CurrentResult) : ReportExporter.ToMarkdown(CurrentResult);
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(dialog.FilterIndex == 2));
            status.Text = "报告已保存至所选文件。";
            status.Foreground = Accent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status.Text = "保存失败，" + exception.Message;
            status.Foreground = ColorBrush("#B23E36");
        }
    }

    public void RunSmokeChecks(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var results = new List<string>();
        SetInputs(new BuckInputs());
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult is null || !exportButton.IsEnabled || CurrentResult.Inputs.VinMax != 16)
            throw new InvalidOperationException("默认参数经计算按钮未获得有效结果。");
        results.Add("PASS default inputs through Calculate button");

        fields["Vout"].Text = "20";
        if (CurrentResult is not null || exportButton.IsEnabled)
            throw new InvalidOperationException("参数更改后旧结果仍可导出。");
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult is not null || string.IsNullOrWhiteSpace(status.Text))
            throw new InvalidOperationException("非法电压输入未给出错误。");
        results.Add("PASS invalid voltage and stale report prevention");

        fields["Vout"].Text = "NaN";
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult is not null || exportButton.IsEnabled)
            throw new InvalidOperationException("非有限数字通过了界面校验。");
        results.Add("PASS non-finite UI input rejected");

        SetInputs(new BuckInputs { IoutOperating = 0.01 });
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult is null || !CurrentResult.OperatingMode.StartsWith("DCM", StringComparison.Ordinal))
            throw new InvalidOperationException("轻载模式检查失败。");
        results.Add("PASS light-load mode displayed");

        secondExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult is null || CurrentResult.Inputs.Vout != 12)
            throw new InvalidOperationException("第二示例加载失败。");
        results.Add("PASS second example selected");

        firstExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var finalResult = CurrentResult ?? throw new InvalidOperationException("默认示例恢复失败。");
        File.WriteAllText(System.IO.Path.Combine(outputDirectory, "sample-report.md"), ReportExporter.ToMarkdown(finalResult), new UTF8Encoding(false));
        File.WriteAllText(System.IO.Path.Combine(outputDirectory, "sample-report.csv"), ReportExporter.ToCsv(finalResult), new UTF8Encoding(true));
        results.Add("PASS current result exported as Markdown and UTF-8 CSV");

        resultScroll.ScrollToTop();
        Width = 1360;
        Height = 920;
        UpdateLayout();
        DrawWaveform();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-default.png"));
        if (waveform.Children.Count == 0)
            throw new InvalidOperationException("默认工况电感波形未绘制。");
        resultScroll.ScrollToEnd();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-details.png"));
        resultScroll.ScrollToTop();
        Width = 1000;
        Height = 700;
        UpdateLayout();
        DrawWaveform();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-small.png"));
        resultScroll.ScrollToEnd();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-small-details.png"));
        results.Add("PASS large and minimum window layout rendered");
        File.WriteAllLines(System.IO.Path.Combine(outputDirectory, "smoke-results.txt"), results, new UTF8Encoding(false));
    }

    private void SaveScreenshot(string path)
    {
        var target = (FrameworkElement)Content;
        target.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(target.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(target.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Accent,
        Margin = new Thickness(0, 3, 0, 10)
    };

    private static Border Card(UIElement content, double padding) => new()
    {
        Background = Brushes.White,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(padding),
        Child = content
    };

    private static string Number(double value)
    {
        double magnitude = Math.Abs(value);
        string format = magnitude is > 0 and < 0.001 or >= 1e6 ? "G6" : "0.###";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
    private static SolidColorBrush ColorBrush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
