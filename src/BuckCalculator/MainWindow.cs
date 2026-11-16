using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BuckCalculator;

public sealed class MainWindow : Window
{
    private static readonly Brush Ink = ColorBrush("#1D1D1F");
    private static readonly Brush Muted = ColorBrush("#86868B");
    private static readonly Brush Accent = ColorBrush("#007AFF");
    private static readonly Brush Line = ColorBrush("#E5E5EA");
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Dictionary<string, TextBlock> fieldLabels = [];
    private readonly Dictionary<ConverterTopology, Dictionary<string, string>> topologyInputs = [];
    private readonly Button buckButton = new() { Content = "BUCK 降压", Height = 34, MinWidth = 116 };
    private readonly Button boostButton = new() { Content = "BOOST 升压", Height = 34, MinWidth = 116 };
    private readonly TopologyDiagram topologyDiagram = new();
    private readonly TextBlock inputHeadingTitle = new() { Text = "设计参数", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ScrollViewer inputScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock applicationTitle = new() { Text = "BUCK / BOOST 计算器", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = Ink };
    private readonly TextBlock headerSubtitle = new() { Text = "电源参数设计、拓扑与最坏工况检查", FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock topologyTitle = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock formulaText = new() { FontSize = 12, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly Button calculateButton = new() { Content = "计算并检查", Height = 42 };
    private readonly Button exportButton = new() { Content = "导出报告", MinWidth = 104, Height = 36 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 42, FontSize = 12, LineHeight = 19 };
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
    private readonly Button checkUpdatesButton = new() { Content = "检查更新", MinWidth = 92, Height = 36, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button updateActionButton = new() { Content = "下载并更新", MinWidth = 112, Height = 34 };
    private readonly TextBlock updateMessage = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar downloadProgress = new() { Minimum = 0, Maximum = 100, Height = 5, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button parametersTab = new() { Content = "设计参数", Height = 34, MinWidth = 118 };
    private readonly Button resultsTab = new() { Content = "结果与拓扑", Height = 34, MinWidth = 118 };
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly UpdateService updateService = new();
    private readonly bool automaticUpdates;
    private Grid workspace = null!;
    private Grid header = null!;
    private FrameworkElement headerTitle = null!;
    private FrameworkElement headerTools = null!;
    private FrameworkElement exampleTools = null!;
    private FrameworkElement inputPane = null!;
    private Border compactNavigation = null!;
    private Border updateBanner = null!;
    private HwndSource? nativeSource;
    private AppUpdate? availableUpdate;
    private bool checkingUpdates;
    private bool downloadingUpdate;
    private bool compactLayout;
    private bool showingResults;
    private bool windowClosed;
    private bool fittingWindow;
    private bool updatingInputs;
    private ConverterTopology selectedTopology;

    public BuckResult? CurrentResult { get; private set; }

    public MainWindow(bool enableAutomaticUpdates = true)
    {
        automaticUpdates = enableAutomaticUpdates;
        Title = "BUCK / BOOST 计算器 · Epiphany";
        var startupArea = GetStartupWorkArea();
        Width = Math.Max(320, Math.Min(1240, startupArea.Width - 32));
        Height = Math.Max(260, Math.Min(840, startupArea.Height - 32));
        MinWidth = Math.Min(620, Width);
        MinHeight = Math.Min(420, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ColorBrush("#F5F5F7");
        Foreground = Ink;
        FontFamily = new FontFamily("Inter, Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResources();
        status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = status });
        updateMessage.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = updateMessage });

        var layout = new Grid { Background = Background };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(BuildHeader());
        updateBanner = BuildUpdateBanner();
        Grid.SetRow(updateBanner, 1);
        layout.Children.Add(updateBanner);

        workspace = new Grid { Margin = new Thickness(24, 0, 24, 20) };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(318) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(workspace, 2);
        layout.Children.Add(workspace);

        inputPane = (FrameworkElement)BuildInputs();
        Grid.SetRow(inputPane, 1);
        workspace.Children.Add(inputPane);
        var results = BuildResults();
        Grid.SetColumn(results, 2);
        Grid.SetRow(results, 1);
        workspace.Children.Add(results);
        compactNavigation = BuildCompactNavigation();
        workspace.Children.Add(compactNavigation);
        Content = layout;

        calculateButton.Click += (_, _) =>
        {
            Calculate();
            if (CurrentResult is not null && compactLayout)
                ShowCompactPane(true);
        };
        exportButton.Click += (_, _) => ExportReport();
        waveform.SizeChanged += (_, _) => DrawWaveform();
        firstExampleButton.Click += (_, _) => ApplyExample(0);
        secondExampleButton.Click += (_, _) => ApplyExample(1);
        buckButton.Click += (_, _) => SwitchTopology(ConverterTopology.Buck);
        boostButton.Click += (_, _) => SwitchTopology(ConverterTopology.Boost);
        checkUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(true);
        updateActionButton.Click += async (_, _) =>
        {
            if (availableUpdate is null)
                await CheckForUpdatesAsync(true);
            else
                await InstallUpdateAsync();
        };
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        ApplyExample(0);
        Loaded += (_, _) => DrawWaveform();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => FitWindowToWorkArea()));
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(new Action(() => FitWindowToWorkArea()));
        };
        SourceInitialized += (_, _) =>
        {
            nativeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            nativeSource?.AddHook(WindowMessageHook);
        };
        ContentRendered += OnFirstContentRendered;
        Closed += (_, _) =>
        {
            windowClosed = true;
            updateTimer.Stop();
            updateCancellation.Cancel();
            updateCancellation.Dispose();
            nativeSource?.RemoveHook(WindowMessageHook);
        };
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
        header = new Grid { Margin = new Thickness(27, 22, 27, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titlePanel = new StackPanel();
        titlePanel.Children.Add(applicationTitle);
        titlePanel.Children.Add(headerSubtitle);
        header.Children.Add(titlePanel);
        headerTitle = titlePanel;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var examples = new StackPanel { Orientation = Orientation.Horizontal };
        examples.Children.Add(new TextBlock { Text = "示例", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        firstExampleButton.BorderThickness = new Thickness(0);
        secondExampleButton.BorderThickness = new Thickness(0);
        firstExampleButton.FontSize = 12;
        secondExampleButton.FontSize = 12;
        segments.Children.Add(firstExampleButton);
        segments.Children.Add(secondExampleButton);
        examples.Children.Add(new Border { Child = segments, Background = ColorBrush("#E8E8ED"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Margin = new Thickness(0, 0, 12, 0) });
        tools.Children.Add(examples);
        exampleTools = examples;
        tools.Children.Add(checkUpdatesButton);
        tools.Children.Add(exportButton);
        Grid.SetColumn(tools, 1);
        header.Children.Add(tools);
        headerTools = tools;
        return header;
    }

    private Border BuildCompactNavigation()
    {
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        parametersTab.BorderThickness = new Thickness(0);
        resultsTab.BorderThickness = new Thickness(0);
        choices.Children.Add(parametersTab);
        choices.Children.Add(resultsTab);
        parametersTab.Click += (_, _) => ShowCompactPane(false);
        resultsTab.Click += (_, _) => ShowCompactPane(true);
        return new Border
        {
            Child = choices,
            Background = ColorBrush("#E8E8ED"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
    }

    private Border BuildUpdateBanner()
    {
        var contents = new Grid();
        contents.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contents.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contents.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        updateMessage.Margin = new Thickness(0, 0, 12, 0);
        contents.Children.Add(updateMessage);
        updateActionButton.Background = Accent;
        updateActionButton.Foreground = Brushes.White;
        updateActionButton.BorderBrush = Accent;
        Grid.SetColumn(updateActionButton, 1);
        contents.Children.Add(updateActionButton);
        Grid.SetRow(downloadProgress, 1);
        Grid.SetColumnSpan(downloadProgress, 2);
        contents.Children.Add(downloadProgress);
        return new Border
        {
            Background = ColorBrush("#EDF5FF"),
            BorderBrush = ColorBrush("#DCEAFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(24, 0, 24, 12),
            Child = contents,
            Visibility = Visibility.Collapsed
        };
    }

    private void UpdateResponsiveLayout()
    {
        if (workspace is null || compactNavigation is null || headerTools is null)
            return;
        double width = ActualWidth;
        compactLayout = width < 1000;
        workspace.Margin = new Thickness(compactLayout ? 16 : 24, 0, compactLayout ? 16 : 24, 16);
        workspace.ColumnDefinitions[0].Width = compactLayout ? new GridLength(1, GridUnitType.Star) : new GridLength(318);
        workspace.ColumnDefinitions[1].Width = new GridLength(compactLayout ? 0 : 20);
        workspace.ColumnDefinitions[2].Width = compactLayout ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(resultScroll, compactLayout ? 0 : 2);
        compactNavigation.Visibility = compactLayout ? Visibility.Visible : Visibility.Collapsed;
        inputPane.Visibility = compactLayout && showingResults ? Visibility.Collapsed : Visibility.Visible;
        resultScroll.Visibility = compactLayout && !showingResults ? Visibility.Collapsed : Visibility.Visible;
        parametersTab.Background = showingResults ? Brushes.Transparent : Brushes.White;
        parametersTab.Foreground = showingResults ? Muted : Ink;
        resultsTab.Background = showingResults ? Brushes.White : Brushes.Transparent;
        resultsTab.Foreground = showingResults ? Ink : Muted;
        bool shortLayout = ActualHeight < 560;
        bool stackedHeader = width < 1000 && !shortLayout;
        applicationTitle.FontSize = shortLayout ? 20 : 25;
        status.MaxHeight = shortLayout ? 19 : 42;
        updateMessage.MaxHeight = shortLayout ? 32 : 40;
        headerSubtitle.Visibility = shortLayout ? Visibility.Collapsed : Visibility.Visible;
        exampleTools.Visibility = shortLayout ? Visibility.Collapsed : Visibility.Visible;
        inputHeadingTitle.Visibility = shortLayout ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(headerTitle, stackedHeader ? 2 : 1);
        Grid.SetRow(headerTools, stackedHeader ? 1 : 0);
        Grid.SetColumn(headerTools, stackedHeader ? 0 : 1);
        Grid.SetColumnSpan(headerTools, stackedHeader ? 2 : 1);
        headerTools.Margin = new Thickness(0, stackedHeader ? 12 : 0, 0, 0);
        header.Margin = new Thickness(compactLayout ? 18 : 27, shortLayout ? 10 : 18, compactLayout ? 18 : 27, shortLayout ? 10 : 16);
        DrawWaveform();
    }

    private void ShowCompactPane(bool results)
    {
        showingResults = results;
        UpdateResponsiveLayout();
        UpdateLayout();
        DrawWaveform();
    }

    private async void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        FitWindowToWorkArea(center: true);
        UpdateResponsiveLayout();
        if (automaticUpdates)
        {
            updateTimer.Start();
            await CheckForUpdatesAsync(false);
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdates || downloadingUpdate || windowClosed)
            return;
        checkingUpdates = true;
        checkUpdatesButton.IsEnabled = false;
        updateActionButton.IsEnabled = false;
        checkUpdatesButton.Content = "正在检查…";
        try
        {
            var update = await updateService.CheckAsync(updateCancellation.Token);
            if (windowClosed)
                return;
            DisplayUpdateCheckResult(update, manual);
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (manual && !windowClosed)
            {
                updateBanner.Visibility = Visibility.Visible;
                updateMessage.Text = "检查更新失败，请检查网络连接后重试。";
                updateMessage.ToolTip = exception.Message;
                updateActionButton.Content = availableUpdate is null ? "重试检查" : "下载并更新";
                updateActionButton.Visibility = Visibility.Visible;
                updateActionButton.IsEnabled = true;
            }
        }
        finally
        {
            checkingUpdates = false;
            if (!windowClosed)
            {
                checkUpdatesButton.Content = "检查更新";
                checkUpdatesButton.IsEnabled = !downloadingUpdate;
                updateActionButton.IsEnabled = !downloadingUpdate;
            }
        }
    }

    private void DisplayUpdateCheckResult(AppUpdate? update, bool manual)
    {
        availableUpdate = update;
        downloadProgress.Visibility = Visibility.Collapsed;
        updateMessage.ToolTip = update?.ReleaseNotes;
        if (update is not null)
        {
            updateBanner.Visibility = Visibility.Visible;
            updateMessage.Text = $"发现新版本 {update.VersionText}，下载并安装后将自动重新启动。";
            updateActionButton.Content = "下载并更新";
            updateActionButton.Visibility = Visibility.Visible;
            updateActionButton.IsEnabled = true;
            return;
        }
        updateActionButton.Visibility = Visibility.Collapsed;
        updateBanner.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        updateMessage.Text = manual ? $"当前已是最新版本 {UpdateService.CurrentVersion.ToString(3)}。" : "";
    }

    private async Task InstallUpdateAsync()
    {
        var update = availableUpdate;
        if (update is null || downloadingUpdate || windowClosed)
            return;
        downloadingUpdate = true;
        updateActionButton.IsEnabled = false;
        checkUpdatesButton.IsEnabled = false;
        downloadProgress.Value = 0;
        downloadProgress.Visibility = Visibility.Visible;
        updateMessage.Text = $"正在下载版本 {update.VersionText}，0%。";
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (windowClosed || !downloadingUpdate)
                    return;
                double percent = Math.Clamp(value, 0, 1) * 100;
                downloadProgress.Value = percent;
                updateMessage.Text = $"正在下载版本 {update.VersionText}，{percent:0}%。";
            });
            string installer = await updateService.DownloadAsync(update, progress, updateCancellation.Token);
            updateCancellation.Token.ThrowIfCancellationRequested();
            updateMessage.Text = "下载与校验完成，正在启动更新安装程序。";
            UpdateService.LaunchInstaller(installer, update.Sha256);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!windowClosed)
            {
                updateMessage.Text = "更新失败，可重试下载并校验安装包。";
                updateMessage.ToolTip = exception.Message;
                updateActionButton.Content = "重试更新";
            }
        }
        finally
        {
            downloadingUpdate = false;
            if (!windowClosed)
            {
                updateActionButton.IsEnabled = true;
                checkUpdatesButton.IsEnabled = true;
            }
        }
    }

    private UIElement BuildInputs()
    {
        var panel = new Grid { Margin = new Thickness(18) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var inputHeading = new StackPanel();
        inputHeading.Children.Add(inputHeadingTitle);
        var topologyChoices = new UniformGrid { Columns = 2 };
        buckButton.BorderThickness = new Thickness(0);
        boostButton.BorderThickness = new Thickness(0);
        topologyChoices.Children.Add(buckButton);
        topologyChoices.Children.Add(boostButton);
        inputHeading.Children.Add(new Border { Child = topologyChoices, Background = ColorBrush("#E8E8ED"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 14) });

        var inputFields = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        inputFields.Children.Add(inputHeading);
        inputFields.Children.Add(SectionLabel("电源规格"));
        inputFields.Children.Add(FieldPair(("VinMin", "输入下限", "V"), ("VinMax", "输入上限", "V")));
        inputFields.Children.Add(FieldPair(("Vout", "输出电压", "V"), ("IoutMax", "额定输出电流", "A")));
        inputFields.Children.Add(FieldPair(("FrequencyKhz", "开关频率", "kHz"), ("IoutOperating", "检查工作负载", "A")));
        inputFields.Children.Add(SectionLabel("纹波与容差"));
        inputFields.Children.Add(FieldPair(("RipplePercent", "电感纹波目标", "%"), ("OutputRippleMv", "输出纹波预算", "mVpp")));
        inputFields.Children.Add(new TextBlock { Text = "纹波目标为 ΔIL 峰峰值占平均电感电流的比例。BUCK 的平均值为 Iout，BOOST 按各输入电压下的 IL,avg 计算。", Foreground = Muted, FontSize = 11.5, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -2, 0, 10) });
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
        inputScroll.Content = inputFields;
        Grid.SetRow(inputScroll, 1);
        panel.Children.Add(inputScroll);

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
        var topologyContent = new StackPanel();
        topologyContent.Children.Add(topologyTitle);
        topologyContent.Children.Add(topologyDiagram);
        topologyContent.Children.Add(new TextBlock { Text = "理想非同步功率级。Q 为受控开关，D 的短线端为阴极。", FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var topologyCard = Card(topologyContent, 18);
        topologyCard.Margin = new Thickness(0, 0, 0, 14);
        results.Children.Add(topologyCard);
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
        formulas.Content = formulaText;
        results.Children.Add(formulas);
        string version = UpdateService.CurrentVersion.ToString(3);
        results.Children.Add(new TextBlock { Text = $"BUCK / BOOST Calculator {version}  ·  Epiphany", FontSize = 11, Foreground = Muted, Margin = new Thickness(2, 14, 0, 4) });
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
        var fieldLabel = new TextBlock { Text = label, Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap };
        fieldLabels.Add(key, fieldLabel);
        panel.Children.Add(fieldLabel);
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
        waveformDescription.Text = "计算后显示同一输入工况、电感下限下的稳态波形。";
    }

    private void ApplyExample(int index)
    {
        firstExampleButton.Background = index == 0 ? Brushes.White : Brushes.Transparent;
        firstExampleButton.Foreground = index == 0 ? Ink : Muted;
        secondExampleButton.Background = index == 1 ? Brushes.White : Brushes.Transparent;
        secondExampleButton.Foreground = index == 1 ? Ink : Muted;
        BuckInputs input;
        if (selectedTopology == ConverterTopology.Boost)
        {
            input = index == 1
                ? new BuckInputs { Topology = ConverterTopology.Boost, VinMin = 9, VinMax = 16, Vout = 24, IoutMax = 1, IoutOperating = 1, FrequencyKhz = 300, OutputRippleMv = 100, EsrMilliohm = 5 }
                : new BuckInputs { Topology = ConverterTopology.Boost, VinMin = 5, VinMax = 9, Vout = 12, IoutMax = 1, IoutOperating = 1, OutputRippleMv = 60, EsrMilliohm = 5 };
        }
        else
        {
            input = index == 1
                ? new BuckInputs { VinMin = 18, VinMax = 30, Vout = 12, IoutMax = 3, IoutOperating = 3, FrequencyKhz = 300, OutputRippleMv = 30 }
                : new BuckInputs();
        }
        SetInputs(input);
        Calculate();
        resultScroll.ScrollToTop();
        inputScroll.ScrollToTop();
    }

    private void SetInputs(BuckInputs input)
    {
        selectedTopology = input.Topology;
        UpdateTopologyDisplay();
        updatingInputs = true;
        foreach (var property in typeof(BuckInputs).GetProperties())
        {
            if (fields.TryGetValue(property.Name, out var field))
                field.Text = Convert.ToDouble(property.GetValue(input), CultureInfo.InvariantCulture).ToString("G10", CultureInfo.InvariantCulture);
        }
        updatingInputs = false;
        MarkInputsChanged();
    }

    private void SwitchTopology(ConverterTopology topology)
    {
        if (selectedTopology == topology)
            return;
        topologyInputs[selectedTopology] = fields.ToDictionary(field => field.Key, field => field.Value.Text);
        selectedTopology = topology;
        UpdateTopologyDisplay();
        if (topologyInputs.TryGetValue(topology, out var previousInputs))
        {
            updatingInputs = true;
            foreach (var field in fields)
                field.Value.Text = previousInputs[field.Key];
            updatingInputs = false;
            firstExampleButton.Background = Brushes.Transparent;
            firstExampleButton.Foreground = Muted;
            secondExampleButton.Background = Brushes.Transparent;
            secondExampleButton.Foreground = Muted;
            MarkInputsChanged();
            Calculate();
        }
        else
        {
            ApplyExample(0);
        }
        resultScroll.ScrollToTop();
    }

    private void UpdateTopologyDisplay()
    {
        bool boost = selectedTopology == ConverterTopology.Boost;
        buckButton.Background = boost ? Brushes.Transparent : Brushes.White;
        buckButton.Foreground = boost ? Muted : Ink;
        boostButton.Background = boost ? Brushes.White : Brushes.Transparent;
        boostButton.Foreground = boost ? Ink : Muted;
        firstExampleButton.Content = boost ? "12 V / 1 A" : "5 V / 2 A";
        firstExampleButton.ToolTip = boost ? "载入 5–9 V 输入、12 V / 1 A 升压示例" : "载入 9–16 V 输入、5 V / 2 A 降压示例";
        secondExampleButton.Content = boost ? "24 V / 1 A" : "12 V / 3 A";
        secondExampleButton.ToolTip = boost ? "载入 9–16 V 输入、24 V / 1 A 升压示例" : "载入 18–30 V 输入、12 V / 3 A 降压示例";
        string rippleLabel = boost ? "纹波 / 当地 IL,avg" : "纹波 / 额定 Iout";
        fieldLabels["RipplePercent"].Text = rippleLabel;
        fields["RipplePercent"].ToolTip = boost ? "ΔIL / IL,avg × 100%，在输入区间各工况满足目标" : "ΔIL / Iout,max × 100%";
        System.Windows.Automation.AutomationProperties.SetName(fields["RipplePercent"], rippleLabel);
        topologyTitle.Text = boost ? "BOOST 升压拓扑" : "BUCK 降压拓扑";
        topologyDiagram.SetTopology(selectedTopology);
        formulaText.Text = "固定频率、理想 CCM 稳态模型。以下关系使用 SI 单位，ΔIL 为电感电流峰峰值。\n\n"
            + ReportExporter.GetFormulaText(selectedTopology)
            + "\n\n元件选型需核对控制器占空比与最小导通时间、开关损耗、器件耐压、电感饱和和温升、电容偏置曲线及负载暂态。";
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
        Topology = selectedTopology,
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
            var input = ReadInputs();
            var result = input.Topology == ConverterTopology.Boost ? BoostMath.Calculate(input) : BuckMath.Calculate(input);
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
        modeStatus.Text = $"工作负载 {Number(result.Inputs.IoutOperating)} A · {result.OperatingMode}    |    最坏工况临界输出电流 {Number(result.BoundaryCurrent)} A";
        modeStatus.Foreground = result.OperatingMode.StartsWith("CCM", StringComparison.Ordinal) ? Accent : ColorBrush("#A56013");
        waveformDescription.Text = $"Vin = {Number(result.WaveformVin)} V  ·  Lmin = {Number(result.MinimumInductanceUh)} µH  ·  IL,avg = {Number(result.WaveformAverageCurrent)} A  ·  ΔIL = {Number(result.WaveformRipple)} App  ·  周期 {Number(1000 / result.Inputs.FrequencyKhz)} µs";
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
        double maxCurrent = result.WaveformPeakCurrent * 1.12;
        double minCurrent = Math.Max(0, result.WaveformValleyCurrent - result.WaveformRipple * 0.38);
        double span = Math.Max(result.WaveformRipple * 1.7, maxCurrent - minCurrent);
        maxCurrent = minCurrent + span;
        double Y(double current) => top + plotHeight * (maxCurrent - current) / span;

        double duty = result.WaveformDuty;
        double periodWidth = plotWidth / 2;
        var onRegion = new Rectangle { Width = periodWidth * duty, Height = plotHeight, Fill = ColorBrush("#F0F6FF") };
        Canvas.SetLeft(onRegion, left);
        Canvas.SetTop(onRegion, top);
        waveform.Children.Add(onRegion);
        foreach (double current in new[] { result.WaveformValleyCurrent, result.WaveformAverageCurrent, result.WaveformPeakCurrent })
        {
            AddLine(left, Y(current), left + plotWidth, Y(current), Line, 1, current == result.WaveformAverageCurrent);
            AddPlotText(Number(current) + " A", 0, Y(current) - 8, Muted);
        }
        AddLine(left, top + plotHeight, left + plotWidth, top + plotHeight, Line, 1);
        var trace = new Polyline { Stroke = Accent, StrokeThickness = 2.8, StrokeLineJoin = PenLineJoin.Round };
        for (int cycle = 0; cycle < 2; cycle++)
        {
            double start = left + periodWidth * cycle;
            trace.Points.Add(new Point(start, Y(result.WaveformValleyCurrent)));
            trace.Points.Add(new Point(start + periodWidth * duty, Y(result.WaveformPeakCurrent)));
            trace.Points.Add(new Point(start + periodWidth, Y(result.WaveformValleyCurrent)));
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
            Title = $"导出本轮 {CurrentResult.Inputs.Topology.ToString().ToUpperInvariant()} 计算报告",
            FileName = $"{CurrentResult.Inputs.Topology.ToString().ToUpperInvariant()}-{Number(CurrentResult.Inputs.Vout)}V-{Number(CurrentResult.Inputs.IoutMax)}A-计算报告",
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
        FitWindowToWorkArea();
        UpdateLayout();
        AssertWindowFitsWorkArea(results, "startup");
        double startupWidth = Width;
        double startupHeight = Height;
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

        topologyDiagram.AssertElectricalLayout();
        if (topologyDiagram.Topology != ConverterTopology.Buck)
            throw new InvalidOperationException("默认 BUCK 拓扑未显示。");
        results.Add("PASS BUCK topology labels, high-side switch and freewheel diode polarity");
        ShowCompactPane(true);
        resultScroll.ScrollToTop();
        UpdateLayout();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "buck-topology.png"), topologyDiagram);

        fields["VinMin"].Text = "unfinished";
        boostButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult?.Inputs.Topology != ConverterTopology.Boost || CurrentResult.Inputs.Vout != 12 || selectedTopology != ConverterTopology.Boost)
            throw new InvalidOperationException("首次切换 BOOST 未载入有效升压示例。");
        topologyDiagram.AssertElectricalLayout();
        if (topologyDiagram.Topology != ConverterTopology.Boost || !fieldLabels["RipplePercent"].Text.Contains("IL,avg", StringComparison.Ordinal))
            throw new InvalidOperationException("BOOST 拓扑或纹波比例口径未切换。");
        results.Add("PASS BOOST selection through segmented button, local-average ripple label and low-side topology");
        var boostResult = CurrentResult;
        if (boostResult.WaveformVin != boostResult.Inputs.VinMin
            || Math.Abs(boostResult.WaveformDuty - (1 - boostResult.WaveformVin / boostResult.Inputs.Vout)) > 1e-10
            || Math.Abs(boostResult.WaveformAverageCurrent - boostResult.Inputs.IoutMax * boostResult.Inputs.Vout / boostResult.WaveformVin) > 1e-10
            || Math.Abs(boostResult.WaveformPeakCurrent - boostResult.WaveformAverageCurrent - boostResult.WaveformRipple / 2) > 1e-10)
            throw new InvalidOperationException("BOOST 波形未使用同一输入工况的数据。");
        DrawWaveform();
        var boostTrace = waveform.Children.OfType<Polyline>().Single();
        double expectedPeakX = 62 + (waveform.ActualWidth - 87) / 2 * boostResult.WaveformDuty;
        if (boostTrace.Points.Count != 6 || Math.Abs(boostTrace.Points[1].X - expectedPeakX) > 0.1
            || !waveform.Children.OfType<TextBlock>().Any(label => label.Text == Number(boostResult.WaveformAverageCurrent) + " A"))
            throw new InvalidOperationException("BOOST 波形绘图的占空比或平均电流参考线错误。");
        results.Add("PASS BOOST waveform duty, average and extrema belong to the same Vin operating point");
        File.WriteAllText(System.IO.Path.Combine(outputDirectory, "boost-report.md"), ReportExporter.ToMarkdown(boostResult), new UTF8Encoding(false));
        File.WriteAllText(System.IO.Path.Combine(outputDirectory, "boost-report.csv"), ReportExporter.ToCsv(boostResult), new UTF8Encoding(true));
        if (!ReportExporter.ToMarkdown(boostResult).StartsWith("# BOOST", StringComparison.Ordinal)
            || !ReportExporter.ToCsv(boostResult).Contains("\"BOOST\"", StringComparison.Ordinal)
            || !formulaText.Text.Contains("D = 1 − Vin / Vout", StringComparison.Ordinal))
            throw new InvalidOperationException("BOOST 报告或界面公式未切换。");
        results.Add("PASS BOOST-specific Markdown, CSV and shared formula text");
        resultScroll.ScrollToTop();
        ShowCompactPane(true);
        UpdateLayout();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "boost-desktop.png"));
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "boost-topology.png"), topologyDiagram);
        secondExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult?.Inputs.Topology != ConverterTopology.Boost || CurrentResult.Inputs.Vout != 24 || CurrentResult.Inputs.VinMax != 16)
            throw new InvalidOperationException("BOOST 第二示例加载失败。");
        results.Add("PASS BOOST second example selected with 9–16 V input and 24 V output");
        firstExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        fields["Vout"].Text = "4";
        if (CurrentResult is not null || exportButton.IsEnabled)
            throw new InvalidOperationException("BOOST 参数更改后仍可导出旧结果。");
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult is not null || exportButton.IsEnabled || topologyDiagram.Topology != ConverterTopology.Boost)
            throw new InvalidOperationException("非法升压电压通过校验或拓扑随错误消失。");
        topologyDiagram.AssertElectricalLayout();
        results.Add("PASS invalid BOOST voltage clears the result while keeping topology available");

        buckButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (fields["VinMin"].Text != "unfinished" || CurrentResult is not null || exportButton.IsEnabled)
            throw new InvalidOperationException("切换模式时未保留 BUCK 尚未完成的输入。");
        fields["VinMin"].Text = "10";
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        boostButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (fields["Vout"].Text != "4" || CurrentResult is not null)
            throw new InvalidOperationException("切换模式时未保留 BOOST 非法输入。");
        firstExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        buckButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (CurrentResult?.Inputs.VinMin != 10 || CurrentResult.Inputs.Topology != ConverterTopology.Buck)
            throw new InvalidOperationException("BUCK 参数恢复后未重新计算。");
        results.Add("PASS each topology preserves its own valid and unfinished input text across switching");
        firstExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        resultScroll.ScrollToTop();
        ShowCompactPane(false);
        UpdateLayout();
        DrawWaveform();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-default.png"));
        ShowCompactPane(true);
        if (waveform.Children.Count == 0)
            throw new InvalidOperationException("默认工况电感波形未绘制。");
        resultScroll.ScrollToEnd();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-details.png"));
        resultScroll.ScrollToTop();
        CheckCompactLayout(outputDirectory, results, 900, 600, "desktop-small");
        CheckCompactLayout(outputDirectory, results, 760, 540, "desktop-compact");
        resultScroll.ScrollToEnd();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-small-details.png"));
        boostButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        firstExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        CheckCompactLayout(outputDirectory, results, 900, 600, "boost-small");
        CheckCompactLayout(outputDirectory, results, 760, 540, "boost-compact");
        buckButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        firstExampleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var previewVersion = new Version(UpdateService.CurrentVersion.Major, UpdateService.CurrentVersion.Minor, Math.Max(0, UpdateService.CurrentVersion.Build) + 1);
        var previewUpdate = new AppUpdate(previewVersion, previewVersion.ToString(3),
            new Uri("https://github.com/Epiphany-Lucky/buck-calculator/releases/download/ui-preview/installer.exe"),
            new string('0', 64), 100_000_000, "更新通知布局烟测，执行期间禁用网络检查与安装。",
            new Uri("https://github.com/Epiphany-Lucky/buck-calculator/releases"));
        DisplayUpdateCheckResult(previewUpdate, false);
        ShowCompactPane(false);
        UpdateLayout();
        AssertComputeButtonInClient();
        if (updateBanner.Visibility != Visibility.Visible || updateActionButton.Visibility != Visibility.Visible || availableUpdate != previewUpdate)
            throw new InvalidOperationException("可用更新通知未保持显示。");
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, "desktop-update-notice.png"));
        results.Add("PASS persistent update notice and visible Calculate footer in compact window");
        DisplayUpdateCheckResult(null, false);
        UpdateLayout();
        if (availableUpdate is not null || updateBanner.Visibility != Visibility.Collapsed || updateActionButton.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("后台检查无可用更新后仍显示旧版本通知。");
        results.Add("PASS automatic no-update response clears the previous persistent notice");
        Width = 620;
        Height = 420;
        FitWindowToWorkArea();
        foreach (var topology in new[] { ConverterTopology.Buck, ConverterTopology.Boost })
        foreach (bool withUpdate in new[] { false, true })
        {
            SwitchTopology(topology);
            ApplyExample(0);
            DisplayUpdateCheckResult(withUpdate ? previewUpdate : null, false);
            downloadProgress.Visibility = withUpdate ? Visibility.Visible : Visibility.Collapsed;
            if (withUpdate)
                updateMessage.Text = string.Concat(Enumerable.Repeat("更新服务状态说明，请检查网络连接后重试。", 20));
            ShowCompactPane(false);
            UpdateLayout();
            if (inputScroll.ViewportHeight < 36)
                throw new InvalidOperationException("最小窗口的参数滚动区不足一行输入框高度。");
            inputScroll.ScrollToEnd();
            UpdateLayout();
            fields["Vout"].BringIntoView();
            UpdateLayout();
            var entryBounds = fields["Vout"].TransformToAncestor(inputScroll).TransformBounds(new Rect(fields["Vout"].RenderSize));
            if (entryBounds.Top < -1 || entryBounds.Bottom > inputScroll.ActualHeight + 1)
                throw new InvalidOperationException("最小窗口中无法滚动到完整参数输入框。");
            inputScroll.ScrollToTop();
            UpdateLayout();
            var modeBounds = buckButton.TransformToAncestor(inputScroll).TransformBounds(new Rect(buckButton.RenderSize));
            if (modeBounds.Top < 0 || modeBounds.Bottom > inputScroll.ActualHeight + 1)
                throw new InvalidOperationException("最小窗口中无法滚动到拓扑切换按钮。");
            calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (CurrentResult?.Inputs.Topology != topology)
                throw new InvalidOperationException("最小窗口计算按钮未执行所选拓扑计算。");
            ShowCompactPane(false);
            status.Text = string.Concat(Enumerable.Repeat("完整状态说明可悬停查看。", 30));
            UpdateLayout();
            AssertComputeButtonInClient();
            AssertWindowFitsWorkArea(results, $"minimum {topology} with update {withUpdate}");
            SaveScreenshot(System.IO.Path.Combine(outputDirectory, $"minimum-{topology}-{(withUpdate ? "update" : "plain")}.png"));
            results.Add($"PASS {topology} minimum 620x420 DIP input rows, mode navigation and Calculate footer with update {withUpdate}");
        }
        downloadProgress.Visibility = Visibility.Collapsed;
        DisplayUpdateCheckResult(null, false);
        SwitchTopology(ConverterTopology.Buck);
        ApplyExample(0);
        Width = startupWidth;
        Height = startupHeight;
        FitWindowToWorkArea();
        UpdateLayout();
        AssertWindowFitsWorkArea(results, "restored");
        results.Add("PASS current monitor sizing, responsive layout, compact calculation and restored bounds");
        File.WriteAllLines(System.IO.Path.Combine(outputDirectory, "smoke-results.txt"), results, new UTF8Encoding(false));
    }

    private void CheckCompactLayout(string outputDirectory, List<string> results, double width, double height, string fileName)
    {
        Width = width;
        Height = height;
        FitWindowToWorkArea();
        ShowCompactPane(false);
        UpdateLayout();
        AssertWindowFitsWorkArea(results, $"requested {width}x{height} DIP");
        if (!compactLayout || inputPane.Visibility != Visibility.Visible || calculateButton.ActualHeight < 40)
            throw new InvalidOperationException("紧凑窗口的参数页或计算按钮不可用。");
        AssertComputeButtonInClient();
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, fileName + "-parameters.png"));
        double operatingLoad = Read("IoutMax") * 0.9;
        fields["IoutOperating"].Text = operatingLoad.ToString("G10", CultureInfo.InvariantCulture);
        calculateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        UpdateLayout();
        DrawWaveform();
        UpdateLayout();
        if (CurrentResult?.Inputs.IoutOperating != operatingLoad || resultScroll.Visibility != Visibility.Visible || !exportButton.IsEnabled || waveform.Children.Count == 0)
            throw new InvalidOperationException("紧凑窗口计算后结果页、导出或波形不可用。");
        resultScroll.ScrollToTop();
        UpdateLayout();
        topologyDiagram.AssertElectricalLayout();
        var plotBounds = topologyDiagram.TransformToAncestor(resultScroll).TransformBounds(new Rect(topologyDiagram.RenderSize));
        if (plotBounds.Left < 0 || plotBounds.Right > resultScroll.ActualWidth + 1 || topologyDiagram.ActualHeight <= 0 || resultScroll.ScrollableHeight <= 0)
            throw new InvalidOperationException("紧凑窗口拓扑图超出横向范围或结果无法纵向滚动。");
        SaveScreenshot(System.IO.Path.Combine(outputDirectory, fileName + ".png"));
        results.Add($"PASS {selectedTopology} compact calculation, topology, scrolling and visible footer at {Width:0}x{Height:0} DIP");
    }

    private void AssertComputeButtonInClient()
    {
        var content = (FrameworkElement)Content;
        var buttonBounds = calculateButton.TransformToAncestor(content).TransformBounds(new Rect(calculateButton.RenderSize));
        if (calculateButton.ActualHeight < 40 || buttonBounds.Top < 0 || buttonBounds.Bottom > content.ActualHeight || buttonBounds.Left < 0 || buttonBounds.Right > content.ActualWidth)
            throw new InvalidOperationException("紧凑参数页的计算按钮超出客户区。");
    }

    private void SaveScreenshot(string path, FrameworkElement? element = null)
    {
        var target = element ?? (FrameworkElement)Content;
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

    private static Size GetStartupWorkArea()
    {
        if (GetCursorPos(out var point))
        {
            var monitor = MonitorFromPoint(point, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                double scale = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1;
                return new Size(info.Work.Width / scale, info.Work.Height / scale);
            }
        }
        return SystemParameters.WorkArea.Size;
    }

    private IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x007E or 0x001A or 0x0232)
            Dispatcher.BeginInvoke(new Action(() => FitWindowToWorkArea()));
        return IntPtr.Zero;
    }

    private void FitWindowToWorkArea(bool center = false)
    {
        if (fittingWindow || windowClosed || WindowState != WindowState.Normal)
            return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds))
            return;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        fittingWindow = true;
        try
        {
            double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
            var work = info.Work;
            int availableWidth = Math.Max(1, work.Width - 16);
            int availableHeight = Math.Max(1, work.Height - 16);
            MinWidth = Math.Min(620, availableWidth / scale);
            MinHeight = Math.Min(420, availableHeight / scale);
            int width = Math.Min(bounds.Width, availableWidth);
            int height = Math.Min(bounds.Height, availableHeight);
            int x = center ? work.Left + (work.Width - width) / 2 : Math.Clamp(bounds.Left, work.Left, work.Right - width);
            int y = center ? work.Top + (work.Height - height) / 2 : Math.Clamp(bounds.Top, work.Top, work.Bottom - height);
            if (x != bounds.Left || y != bounds.Top || width != bounds.Width || height != bounds.Height)
                SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0014);
        }
        finally
        {
            fittingWindow = false;
        }
        UpdateResponsiveLayout();
    }

    private void AssertWindowFitsWorkArea(List<string> results, string scenario)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var bounds))
            throw new InvalidOperationException("无法读取真实窗口外框。");
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
            throw new InvalidOperationException("无法读取窗口所在显示器工作区。");
        if (bounds.Left < info.Work.Left || bounds.Top < info.Work.Top || bounds.Right > info.Work.Right || bounds.Bottom > info.Work.Bottom)
            throw new InvalidOperationException($"{scenario} 窗口外框超出工作区，窗口 {bounds}，工作区 {info.Work}。");
        results.Add($"PASS {scenario} native bounds {bounds} within work area {info.Work}, DPI {GetDpiForWindow(handle)}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top})–({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

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
