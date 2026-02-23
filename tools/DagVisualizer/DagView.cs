using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using SomeEngine.Assets.Data;

namespace SomeEngine.DagVisualizer;

public class DagView : Control
{
    private List<GPUCluster> _clusters = new();
    private int _hoverIndex = -1;
    private Dictionary<int, Point> _nodePositions = new();
    
    private Vector _pan = new(0, 0);
    private double _zoom = 1.0;
    private Point _lastPointerPos;
    private bool _isPanning;

    private readonly Dictionary<int, IBrush> _groupBrushes = new();
    private readonly Random _rng = new(42);

    public DagView()
    {
        ClipToBounds = true;
    }

    public void SetClusters(List<GPUCluster> clusters)
    {
        _clusters = clusters;
        _hoverIndex = -1;
        _groupBrushes.Clear();
        _pan = new Vector(0, 0);
        _zoom = 1.0;
        
        CalculateLayout();
        InvalidateVisual();
    }

    private void CalculateLayout()
    {
        _nodePositions.Clear();
        if (_clusters.Count == 0) return;

        var levels = _clusters.GroupBy(c => (int)c.LODLevel).OrderBy(g => g.Key).ToList();
        double height = 2000;
        double width = 5000;

        double yStep = 250;
        
        for (int i = 0; i < levels.Count; i++)
        {
            var levelIndices = _clusters.Select((c, idx) => new { c, idx })
                                        .Where(x => x.c.LODLevel == i)
                                        .ToList();
            
            /*
            if (i < levels.Count - 1)
            {
                // Order clusters by their ParentGroupId to keep groups together
                levelIndices = levelIndices.OrderBy(x => x.c.ParentGroupId).ToList();
            }
            */

            double xStep = Math.Max(120, width / (levelIndices.Count + 1));
            double y = height - (i + 1) * yStep;

            for (int j = 0; j < levelIndices.Count; j++)
            {
                _nodePositions[levelIndices[j].idx] = new Point((j + 1) * xStep, y);
            }
        }
    }

    private IBrush GetGroupBrush(int groupId)
    {
        if (groupId == -1) return Brushes.Gray;
        if (!_groupBrushes.TryGetValue(groupId, out var brush))
        {
            var color = Color.FromRgb((byte)_rng.Next(80, 220), (byte)_rng.Next(80, 220), (byte)_rng.Next(80, 220));
            brush = new SolidColorBrush(color);
            _groupBrushes[groupId] = brush;
        }
        return brush;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pointer = e.GetCurrentPoint(this);
        if (pointer.Properties.IsLeftButtonPressed)
        {
            _isPanning = true;
            _lastPointerPos = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            var delta = pos - _lastPointerPos;
            _pan += delta;
            _lastPointerPos = pos;
            InvalidateVisual();
            e.Handled = true;
        }
        else
        {
            int oldHover = _hoverIndex;
            _hoverIndex = -1;

            var invTransform = GetTotalTransform().Invert();
            var virtualPos = pos.Transform(invTransform);

            foreach (var kvp in _nodePositions)
            {
                if (Math.Abs(virtualPos.X - kvp.Value.X) < 15 / _zoom && Math.Abs(virtualPos.Y - kvp.Value.Y) < 15 / _zoom)
                {
                    _hoverIndex = kvp.Key;
                    break;
                }
            }

            if (oldHover != _hoverIndex) InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double zoomFactor = e.Delta.Y > 0 ? 1.1 : 0.9;
        var pointerPos = e.GetPosition(this);
        
        var transform = GetTotalTransform();
        var virtualPos = pointerPos.Transform(transform.Invert());
        
        _zoom *= zoomFactor;
        _zoom = Math.Clamp(_zoom, 0.001, 100.0);
        
        var newTransform = GetTotalTransform();
        var newPointerPos = virtualPos.Transform(newTransform);
        _pan += (pointerPos - newPointerPos);

        InvalidateVisual();
        e.Handled = true;
    }

    private Matrix GetTotalTransform()
    {
        return Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y);
    }

    public override void Render(DrawingContext context)
    {
        // Draw a hit-testable transparent background to ensure mouse events work everywhere
        // Use local coordinates (0,0) instead of parent-relative Bounds
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (_clusters.Count == 0) return;

        using (context.PushTransform(GetTotalTransform()))
        {
            var pen = new Pen(Brushes.DimGray, 0.5);
            var highlightPen = new Pen(Brushes.Yellow, 2.0);

            /*
            // Draw all DAG connections (Many-to-Many)
            for (int i = 0; i < _clusters.Count; i++)
            {
                var child = _clusters[i];
                if (child.ParentGroupId != -1)
                {
                    for (int j = 0; j < _clusters.Count; j++)
                    {
                        var parent = _clusters[j];
                        if (parent.GroupId == child.ParentGroupId && parent.LODLevel == child.LODLevel + 1)
                        {
                            if (_nodePositions.TryGetValue(i, out var start) && _nodePositions.TryGetValue(j, out var end))
                            {
                                bool isHighlight = false;
                                if (_hoverIndex != -1)
                                {
                                    isHighlight = (_hoverIndex == i || _hoverIndex == j);
                                }
                                context.DrawLine(isHighlight ? highlightPen : pen, start, end);
                            }
                        }
                    }
                }
            }
            */

            // Draw Nodes
            var typeface = new Typeface(FontFamily.Default);
            foreach (var kvp in _nodePositions)
            {
                int idx = kvp.Key;
                var cluster = _clusters[idx];
                var pos = kvp.Value;
                
                var brush = GetGroupBrush(cluster.GroupId);
                bool isHighlight = (idx == _hoverIndex);
                
                if (!isHighlight && _hoverIndex != -1)
                {
                    // var h = _clusters[_hoverIndex];
                    // Highlight if related via DAG (removed)
                }

                context.DrawEllipse(brush, isHighlight ? highlightPen : null, pos, 8, 8);
                
                if (_zoom > 0.3)
                {
                    var text = new FormattedText(
                        $"ID:{idx}\nG:{cluster.GroupId}\nE:{cluster.LODError:F4}",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, 12, Brushes.White);
                    context.DrawText(text, pos + new Point(12, -15));
                }
            }
        }
    }
}
