using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BuckCalculator;

public sealed class TopologyDiagram : Viewbox
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
    private readonly Canvas drawing = new() { Width = 760, Height = 270 };
    private Point diodeAnode;
    private Point diodeCathode;
    private Point switchInput;
    private Point switchOutput;

    public ConverterTopology Topology { get; private set; }

    public TopologyDiagram()
    {
        Stretch = Stretch.Uniform;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Child = drawing;
        SetTopology(ConverterTopology.Buck);
    }

    public void SetTopology(ConverterTopology topology)
    {
        Topology = topology;
        drawing.Children.Clear();
        VoltageSource();
        Wire(56, 84, 200, 84);
        Wire(56, 228, 690, 228);
        Capacitor(140, "Cin");
        Capacitor(580, "Cout");
        Load();
        Wire(580, 84, 690, 84);
        Label("Vout", 560, 43, Accent);
        Ground();

        if (topology == ConverterTopology.Boost)
        {
            Inductor(200, 84);
            Wire(296, 84, 400, 84);
            switchInput = new Point(400, 84);
            switchOutput = new Point(400, 228);
            DrawSwitch(switchInput, switchOutput);
            Label("Q", 427, 147, Accent);
            diodeAnode = new Point(400, 84);
            diodeCathode = new Point(580, 84);
            Diode(diodeAnode, diodeCathode);
            Label("D", 478, 43, Ink);
            Node(400, 84);
            Label("SW", 364, 49, Ink);
            AutomationProperties.SetName(this, "BOOST 升压拓扑");
            AutomationProperties.SetHelpText(this, "Vin 经 L 接 SW。Q 由 SW 接公共地。D 阳极接 SW，阴极接 Vout。Cin、Cout 与负载接公共地。");
        }
        else
        {
            switchInput = new Point(200, 84);
            switchOutput = new Point(340, 84);
            DrawSwitch(switchInput, switchOutput);
            Label("Q", 256, 43, Accent);
            Wire(340, 84, 380, 84);
            Inductor(380, 84);
            Wire(476, 84, 580, 84);
            diodeAnode = new Point(340, 228);
            diodeCathode = new Point(340, 84);
            Diode(diodeAnode, diodeCathode);
            Label("D", 362, 147, Ink);
            Node(340, 84);
            Label("SW", 314, 49, Ink);
            AutomationProperties.SetName(this, "BUCK 降压拓扑");
            AutomationProperties.SetHelpText(this, "Vin 经 Q 接 SW，再经 L 接 Vout。D 阳极接公共地，阴极接 SW。Cin、Cout 与负载接公共地。");
        }
        Node(140, 84);
        Node(580, 84);
        Node(140, 228);
        Node(580, 228);
    }

    internal void AssertElectricalLayout()
    {
        string[] requiredLabels = ["Vin", "Cin", "Q", "D", "L", "Cout", "Rload", "Vout", "GND", "SW"];
        var renderedLabels = drawing.Children.OfType<TextBlock>().Select(label => label.Text).ToHashSet();
        if (requiredLabels.Any(label => !renderedLabels.Contains(label)))
            throw new InvalidOperationException("拓扑图缺少电源、功率器件或公共地标签。");
        bool correctConnections = Topology == ConverterTopology.Boost
            ? diodeAnode == new Point(400, 84) && diodeCathode == new Point(580, 84)
                && switchInput == new Point(400, 84) && switchOutput == new Point(400, 228)
            : diodeAnode == new Point(340, 228) && diodeCathode == new Point(340, 84)
                && switchInput == new Point(200, 84) && switchOutput == new Point(340, 84);
        if (!correctConnections)
            throw new InvalidOperationException("拓扑图的二极管方向或受控开关位置错误。");
    }

    private void VoltageSource()
    {
        Wire(56, 84, 56, 126);
        Wire(56, 174, 56, 228);
        drawing.Children.Add(new Ellipse
        {
            Width = 48, Height = 48, Stroke = Ink, StrokeThickness = 2,
            Margin = new Thickness(32, 126, 0, 0)
        });
        Label("+", 49, 126, Ink);
        Label("−", 49, 148, Ink);
        Label("Vin", 25, 43, Accent);
    }

    private void Capacitor(double x, string name)
    {
        Wire(x, 84, x, 145);
        Wire(x - 16, 145, x + 16, 145);
        Wire(x - 16, 157, x + 16, 157);
        Wire(x, 157, x, 228);
        Label(name, x + 22, 145, Ink);
    }

    private void Load()
    {
        Wire(690, 84, 690, 128);
        drawing.Children.Add(new Rectangle
        {
            Width = 22, Height = 48, Stroke = Ink, StrokeThickness = 2,
            Margin = new Thickness(679, 128, 0, 0)
        });
        Wire(690, 176, 690, 228);
        Label("Rload", 708, 141, Ink);
    }

    private void Ground()
    {
        Wire(460, 228, 460, 243);
        Wire(442, 243, 478, 243);
        Wire(448, 250, 472, 250);
        Wire(455, 257, 465, 257);
        Label("GND", 490, 235, Ink);
        Node(460, 228);
    }

    private void Inductor(double x, double y)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(x, y) };
        for (int turn = 0; turn < 4; turn++)
            figure.Segments.Add(new QuadraticBezierSegment(new Point(x + turn * 24 + 12, y - 34), new Point(x + (turn + 1) * 24, y), true));
        geometry.Figures.Add(figure);
        drawing.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = Accent, StrokeThickness = 2.6, StrokeLineJoin = PenLineJoin.Round });
        Label("L", x + 41, y - 52, Accent);
    }

    private void DrawSwitch(Point input, Point output)
    {
        Vector direction = output - input;
        direction.Normalize();
        Vector normal = new(-direction.Y, direction.X);
        Point center = input + (output - input) * 0.5;
        Point firstContact = center - direction * 21;
        Point secondContact = center + direction * 21;
        Wire(input.X, input.Y, firstContact.X, firstContact.Y);
        Wire(secondContact.X, secondContact.Y, output.X, output.Y);
        Point armEnd = secondContact - normal * 20;
        Wire(firstContact.X, firstContact.Y, armEnd.X, armEnd.Y, Accent, 2.6);
        Node(firstContact.X, firstContact.Y);
        Node(secondContact.X, secondContact.Y);
    }

    private void Diode(Point anode, Point cathode)
    {
        Vector direction = cathode - anode;
        direction.Normalize();
        Vector normal = new(-direction.Y, direction.X);
        Point center = anode + (cathode - anode) * 0.5;
        Point baseCenter = center - direction * 15;
        Point tip = center + direction * 15;
        Wire(anode.X, anode.Y, baseCenter.X, baseCenter.Y);
        Wire(tip.X, tip.Y, cathode.X, cathode.Y);
        drawing.Children.Add(new Polygon
        {
            Points = [baseCenter + normal * 16, baseCenter - normal * 16, tip],
            Stroke = Ink, StrokeThickness = 2, Fill = Brushes.White, StrokeLineJoin = PenLineJoin.Miter
        });
        Point barStart = tip + normal * 19;
        Point barEnd = tip - normal * 19;
        Wire(barStart.X, barStart.Y, barEnd.X, barEnd.Y, Ink, 2.5);
    }

    private void Wire(double x1, double y1, double x2, double y2, Brush? brush = null, double thickness = 2)
    {
        drawing.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush ?? Ink, StrokeThickness = thickness });
    }

    private void Node(double x, double y)
    {
        drawing.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Ink, Margin = new Thickness(x - 3, y - 3, 0, 0) });
    }

    private void Label(string text, double x, double y, Brush brush)
    {
        drawing.Children.Add(new TextBlock { Text = text, Foreground = brush, FontSize = 19, FontFamily = new FontFamily("Inter, Segoe UI"), Margin = new Thickness(x, y, 0, 0) });
    }
}
