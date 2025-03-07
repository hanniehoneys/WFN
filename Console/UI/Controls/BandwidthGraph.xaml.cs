﻿using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;

using SkiaSharp;
using SkiaSharp.Views.WPF;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

using Wokhan.Collections;
using Wokhan.Core;
using Wokhan.WindowsFirewallNotifier.Console.ViewModels;

namespace Wokhan.WindowsFirewallNotifier.Console.UI.Controls;

public partial class BandwidthGraph : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty ConnectionsProperty = DependencyProperty.Register(nameof(Connections), typeof(ObservableCollection<MonitoredConnection>), typeof(BandwidthGraph));

    private const int MAX_DURATION_SEC = 10;

    private ObservableDictionary<string, Tuple<ObservableCollection<CustomDateTimePoint>, ObservableCollection<CustomDateTimePoint>>> allSeries = new();

    private DateTime datetime = DateTime.Now;

    private bool isXPanned;

    private ObservableCollection<CustomDateTimePoint> seriesInTotal = new();

    private ObservableCollection<CustomDateTimePoint> seriesOutTotal = new();

    private Axis xAxis;
    private Axis yAxis;
    private SolidColorPaint crosshairPaint;

#pragma warning disable CS8618 // Non nullable fields are initialized in InitAxes and InitMiniGraph.
    public BandwidthGraph()
#pragma warning restore CS8618 
    {
        InitMiniGraph();

        InitializeComponent();

        this.Loaded += (o, s) => InitAxes();
    }

    private void InitMiniGraph()
    {
        MiniSeries = new List<ISeries> {
            new LineSeries<CustomDateTimePoint>() { Name = "In", Stroke = new SolidColorPaint(SKColors.LightGreen), GeometryStroke = null, GeometryFill = null, Values = seriesInTotal, Mapping = logMapper },
            new LineSeries<CustomDateTimePoint>() { Name = "Out", Stroke = new SolidColorPaint(SKColors.OrangeRed), GeometryStroke = null, GeometryFill = null, Values = seriesOutTotal, Mapping = logMapper }
        };
    }


    private void InitAxes()
    {
        var skAxisPaint = new SolidColorPaint(((SolidColorBrush)Application.Current.Resources[System.Windows.SystemColors.WindowTextBrushKey]).Color.ToSKColor());
        crosshairPaint = new SolidColorPaint(SKColors.Red) { StrokeThickness = 1 };

        xAxis = (Axis)chart.XAxes.First();
        xAxis.Labeler = (time) => new DateTime((long)time).ToString("HH:mm:ss");
        xAxis.TextSize = 10;
        xAxis.LabelsPaint = skAxisPaint;
        xAxis.CrosshairPaint = crosshairPaint;

        xAxis.PropertyChanged += XAxis_PropertyChanged;

        yAxis = (Axis)chart.YAxes.First();
        yAxis.MinLimit = 0;
        yAxis.TextSize = 10;
        yAxis.MinStep = 1;
        yAxis.Labeler = (value) => value == 0 ? "0bps" : UnitFormatter.FormatValue(Math.Pow(10, value), "bps");
        yAxis.LabelsPaint = skAxisPaint;
        yAxis.CrosshairPaint = crosshairPaint;

        var miniYAxis = miniChart.YAxes.First();
        miniYAxis.MinLimit = 0;
        miniYAxis.TextSize = 10;
        miniYAxis.Padding = new LiveChartsCore.Drawing.Padding(0);
        miniYAxis.ShowSeparatorLines = false;
        miniYAxis.Labeler = (value) => value == 0 ? "0bps" : UnitFormatter.FormatValue(Math.Pow(10, value), "bps");

        miniChart.XAxes.First().IsVisible = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double AbsoluteEnd => xAxis?.DataBounds.Max ?? 0;

    public double AbsoluteStart => xAxis?.DataBounds.Min ?? 0;

    public ObservableCollection<MonitoredConnection> Connections
    {
        get => (ObservableCollection<MonitoredConnection>)GetValue(ConnectionsProperty);
        set => SetValue(ConnectionsProperty, value);
    }

    public double CurrentStart
    {
        get => (xAxis is not null ? xAxis.MinLimit + (xAxis.MaxLimit - xAxis.MinLimit) / 2 : 0) ?? 0;
        set
        {
            if (isXPanned)
            {
                var window = (xAxis.MaxLimit - xAxis.MinLimit) / 2;
                xAxis.MinLimit = value - window;
                xAxis.MaxLimit = value + window;

                NotifyPropertyChanged();
            }
        }
    }
    public IEnumerable<ISeries> MiniSeries { get; private set; }
    public ObservableCollection<ISeries> Series { get; } = new();
    public double ThumbSize => (xAxis is not null ? (xAxis.MaxLimit - xAxis.MinLimit) * scrollArea.Track.ActualWidth / (xAxis.DataBounds.Max - xAxis.DataBounds.Min) : 0) ?? 0;

    private string tooltipFormatter(ChartPoint<CustomDateTimePoint, BezierPoint<CircleGeometry>, LabelGeometry> arg)
    {
        return $"{((LineSeries<CustomDateTimePoint>)arg.Context.Series).Tag} - In: {UnitFormatter.FormatValue(arg.PrimaryValue, "bps")} / Out: {UnitFormatter.FormatValue(arg.TertiaryValue, "bps")}";
    }

    private void logMapper(CustomDateTimePoint dateTimePoint, ChartPoint chartPoint)
    {
        chartPoint.SecondaryValue = dateTimePoint.DateTime.Ticks;
        chartPoint.PrimaryValue = dateTimePoint.Value == 0 ? 0 : Math.Log10(dateTimePoint.Value);
        chartPoint.TertiaryValue = dateTimePoint.BandwidthIn;
        chartPoint.QuaternaryValue = dateTimePoint.BandwidthOut;
    }

    DashEffect outDashEffect = new DashEffect(new[] { 2f, 2f });

    public void UpdateGraph()
    {
        datetime = DateTime.Now;

        //Creates a copy of the current connections list to avoid grouping to occur on the UI thread.
        var localConnections = Dispatcher.Invoke(() => Connections.ToList()).GroupBy(connection => $"{connection.ProductName} ({connection.Owner} / {connection.Pid})");
        ulong totalIn = 0;
        ulong totalOut = 0;
        foreach (var connectionGroup in localConnections.AsParallel())
        {
            ObservableCollection<CustomDateTimePoint> seriesInValues;
            ObservableCollection<CustomDateTimePoint> seriesOutValues;

            if (allSeries.TryGetValue(connectionGroup.Key, out var seriesValues))
            {
                (seriesInValues, seriesOutValues) = seriesValues;
            }
            else
            {
                seriesInValues = new();
                seriesOutValues = new();

                var color = connectionGroup.First().Color.ToSKColor();
                var inStroke = new SolidColorPaint(color) { StrokeThickness = 2 };
                var outStroke = new SolidColorPaint(color) { StrokeThickness = 2, PathEffect = outDashEffect };

                Series.Add(new LineSeries<CustomDateTimePoint>() { Tag = connectionGroup.Key, Name = $"{connectionGroup.Key} - In", TooltipLabelFormatter = tooltipFormatter, Fill = null, Stroke = inStroke, GeometryStroke = inStroke, Values = seriesInValues, LineSmoothness = 0, Mapping = logMapper });
                Series.Add(new LineSeries<CustomDateTimePoint>() { Tag = connectionGroup.Key, Name = $"{connectionGroup.Key} - Out", IsVisibleAtLegend = false, IsHoverable = false, Fill = null, Stroke = outStroke, GeometryStroke = outStroke, Values = seriesOutValues, LineSmoothness = 0, Mapping = logMapper });

                allSeries.Add(connectionGroup.Key, Tuple.Create(seriesInValues, seriesOutValues));
            }

            ulong lastSumIn = 0;
            ulong lastSumOut = 0;
            foreach (var connection in connectionGroup)
            {
                lastSumIn += connection.InboundBandwidth;
                lastSumOut += connection.OutboundBandwidth;
            }

            AddAndMergePoints(seriesInValues, lastSumIn, lastSumOut);
            AddAndMergePoints(seriesOutValues, lastSumOut);

            Interlocked.Add(ref totalIn, lastSumIn);
            Interlocked.Add(ref totalOut, lastSumOut);
        }

        seriesInTotal.Add(new CustomDateTimePoint(datetime, totalIn, totalOut));
        seriesOutTotal.Add(new CustomDateTimePoint(datetime, totalOut));

        NotifyPropertyChanged(nameof(AbsoluteStart));
        NotifyPropertyChanged(nameof(AbsoluteEnd));
        NotifyPropertyChanged(nameof(ThumbSize));

        // If scrolling has not been manually forced
        if (!isXPanned)
        {
            Dispatcher.Invoke(() =>
            {
                xAxis.MinLimit = datetime.AddSeconds(-MAX_DURATION_SEC).Ticks;
                // Sets the max position to 1/10th of the max duration value to keep some padding
                xAxis.MaxLimit = datetime.AddSeconds(MAX_DURATION_SEC / 10).Ticks;
            });
            NotifyPropertyChanged(nameof(CurrentStart));
        }
    }

    private void AddAndMergePoints(ObservableCollection<CustomDateTimePoint> series, ulong sum, ulong bandwidthOut = 0)
    {
        if (sum != 0 || series.Count == 0 || series[^1].Value != 0)
        {
            series.Add(new CustomDateTimePoint(datetime, sum, sum, bandwidthOut));
            //if (series.Count > 3 && series[^2].Value == sum && series[^3].Value == sum)
            //{
            //    series.RemoveAt(series.Count - 2);
            //}
        }
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ResetZoom(object sender, RoutedEventArgs e)
    {
        isXPanned = false;
    }

    private void scrollArea_DragStarted(object sender, DragStartedEventArgs e)
    {
        isXPanned = true;
    }

    private void XAxis_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is Axis && (e.PropertyName == nameof(xAxis.MinLimit) || e.PropertyName == nameof(xAxis.MaxLimit)))
        {
            NotifyPropertyChanged(nameof(ThumbSize));
            NotifyPropertyChanged(nameof(CurrentStart));
        }
    }

    private void chart_MouseEnter(object sender, MouseEventArgs e)
    {
        chart.AutoUpdateEnabled = false;
        xAxis.CrosshairPaint = crosshairPaint;
        yAxis.CrosshairPaint = crosshairPaint;
    }

    private void chart_MouseLeave(object sender, MouseEventArgs e)
    {
        chart.AutoUpdateEnabled = true;
        xAxis.CrosshairPaint = null;
        yAxis.CrosshairPaint = null;
    }
}
