using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace CloudSqlProxyManager
{
    /// <summary>
    /// A text box with a dropdown list: type any value, or pick one of the
    /// offered ones. Unlike an editable ComboBox, replacing the list never
    /// touches the text, which keeps Project > Region > Instance in step.
    /// </summary>
    public class ComboField : UserControl
    {
        private readonly TextBox _text = new TextBox();
        private readonly Button _drop = new Button();
        private readonly ListBox _list = new ListBox();
        private readonly Popup _popup = new Popup();
        private List<string> _items = new List<string>();

        /// <summary>Raised when the text changes, by typing or by picking from the list.</summary>
        public event EventHandler TextChanged;

        public ComboField()
        {
            _text.Padding = new Thickness(10, 6, 34, 6);
            _text.VerticalContentAlignment = VerticalAlignment.Center;
            _text.PropertyChanged += (s, e) =>
            {
                if (e.Property == TextBox.TextProperty) TextChanged?.Invoke(this, EventArgs.Empty);
            };
            _text.KeyDown += OnTextKeyDown;

            _drop.Content = new Path
            {
                Data = Geometry.Parse("M 0 0 L 4.5 4.5 L 9 0"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x5F, 0x63, 0x68)),
                StrokeThickness = 1.6,
                Width = 9,
                Height = 5,
                Stretch = Stretch.None
            };
            _drop.Background = Brushes.Transparent;
            _drop.BorderThickness = new Thickness(0);
            _drop.Padding = new Thickness(0);
            _drop.MinWidth = 0;
            _drop.MinHeight = 0;
            _drop.Width = 30;
            _drop.HorizontalAlignment = HorizontalAlignment.Right;
            _drop.VerticalAlignment = VerticalAlignment.Stretch;
            _drop.HorizontalContentAlignment = HorizontalAlignment.Center;
            _drop.VerticalContentAlignment = VerticalAlignment.Center;
            _drop.Focusable = false;
            _drop.Classes.Add("chevron");
            _drop.Margin = new Thickness(0, 1, 1, 1); // inside the text box's border
            _drop.Click += delegate { if (_popup.IsOpen) Close(); else Open(false); };

            // The list: no padding of its own, no sideways scrolling, picked by click or Enter.
            _list.MaxHeight = 240;
            _list.Background = Brushes.White;
            _list.BorderThickness = new Thickness(0);
            _list.Padding = new Thickness(0);
            _list.Margin = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.KeyDown += OnListKeyDown;
            _list.PointerReleased += delegate { if (_list.SelectedItem != null) Pick(); };

            Border frame = new Border();
            frame.Background = Brushes.White;
            frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0xC7, 0xCC));
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(4);
            frame.Padding = new Thickness(0, 4);
            frame.ClipToBounds = true;
            frame.Child = _list;

            _popup.Child = frame;
            _popup.PlacementTarget = this;
            _popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
            _popup.VerticalOffset = 2;
            _popup.IsLightDismissEnabled = true;
            _popup.Closed += delegate { if (!_text.IsFocused) _text.Focus(); };

            Grid grid = new Grid();
            grid.Children.Add(_text);
            grid.Children.Add(_drop);
            grid.Children.Add(_popup);
            Content = grid;
            SetItems(new string[0]);
        }

        public string Text
        {
            get { return _text.Text ?? ""; }
            set { if ((_text.Text ?? "") != (value ?? "")) _text.Text = value ?? ""; }
        }

        public string Watermark
        {
            get { return _text.Watermark; }
            set { _text.Watermark = value; }
        }

        public IReadOnlyList<string> Items
        {
            get { return _items; }
        }

        /// <summary>Replaces the offered values. The text is left alone.</summary>
        public void SetItems(IEnumerable<string> items)
        {
            _items = new List<string>(items);
            _list.ItemsSource = _items;
            _drop.IsEnabled = IsEnabled && _items.Count > 0;
            string label = _items.Count == 1 ? "1 choice" : _items.Count + " choices";
            AutomationProperties.SetName(_drop, label);
            if (_items.Count == 0) Close();
        }

        public new void Focus()
        {
            _text.Focus();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!string.IsNullOrEmpty(Name))
            {
                AutomationProperties.SetAutomationId(_text, Name + "Text");
                AutomationProperties.SetAutomationId(_drop, Name + "Drop");
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsEnabledProperty)
            {
                _text.IsEnabled = IsEnabled;
                _drop.IsEnabled = IsEnabled && _items.Count > 0;
                if (!IsEnabled) Close();
            }
        }

        private void Open(bool fromKeyboard)
        {
            if (_items.Count == 0 || _popup.IsOpen) return;
            _popup.Width = Bounds.Width; // exactly as wide as the field
            _list.SelectedIndex = fromKeyboard ? IndexOfText() : -1;
            _popup.IsOpen = true;
            _list.Focus();
            if (fromKeyboard && _list.SelectedIndex < 0 && _items.Count > 0) _list.SelectedIndex = 0;
        }

        private void Close()
        {
            if (_popup.IsOpen) _popup.IsOpen = false;
        }

        private int IndexOfText()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i], Text.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private void Pick()
        {
            string picked = _list.SelectedItem as string;
            Close();
            if (picked == null) return;
            Text = picked;
            _text.Focus();
            _text.CaretIndex = Text.Length;
        }

        private void OnTextKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Down || (e.Key == Key.F4)) && _items.Count > 0)
            {
                Open(true);
                e.Handled = true;
            }
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                Pick();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                _text.Focus();
                e.Handled = true;
            }
        }
    }
}
