using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Diary.Desktop.Services;

namespace Diary.Desktop.Views;

// Проверка орфографии прямо в редакторе заметки.
// Подключается свойством SpellCheckBehavior.IsEnabled="True" к TextBox:
// слова с ошибками подчёркиваются красной волнистой линией.
// Проверка запускается через 0.4 с после ввода, чтобы не тормозить набор.
public class SpellCheckBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SpellCheckBehavior, TextBox, bool>(
            "IsEnabled", false);

    // Один сервис на все редакторы — словари загружаются один раз.
    private static readonly SpellCheckService Service = new();

    private static readonly ConditionalWeakTable<TextBox, EditorState> States = new();

    static SpellCheckBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnIsEnabledChanged);
    }

    // Классические геттер/сеттер для XAML.
    public static bool GetIsEnabled(TextBox element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            States.GetOrCreateValue(textBox).Attach(textBox, Service);
        }
        else if (States.TryGetValue(textBox, out var state))
        {
            state.Detach();
        }
    }

    // Состояние одного редактора: таймер дебаунса, кэш ошибок, холст-подложка.
    private sealed class EditorState
    {
        private static readonly Pen UnderlinePen = new(
            new SolidColorBrush(Color.Parse("#E5484D")), 1.2);

        private TextBox? _textBox;
        private SpellCheckService? _service;
        private Canvas? _overlay;
        private ScrollViewer? _scroll;
        private readonly DispatcherTimer _debounce;

        // Кэш последней проверки.
        private List<(int Start, int Length, string Word)> _errors = new();

        public EditorState()
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Recheck(); };
        }

        public void Attach(TextBox textBox, SpellCheckService service)
        {
            _textBox = textBox;
            _service = service;

            // Словари грузим в фоне — после загрузки перепроверим текст.
            _ = Task.Run(() =>
            {
                service.EnsureLoaded();
                Dispatcher.UIThread.Post(Schedule);
            });

            EnsureOverlay();
            textBox.TextChanged += OnTextChanged;
            textBox.PropertyChanged += OnTextBoxPropertyChanged;
            Schedule();
        }

        public void Detach()
        {
            if (_textBox is { } textBox)
            {
                textBox.TextChanged -= OnTextChanged;
                textBox.PropertyChanged -= OnTextBoxPropertyChanged;
            }

            if (_scroll is { } scroll) scroll.PropertyChanged -= OnScrollChanged;

            _debounce.Stop();
            _overlay?.Children.Clear();

            if (_overlay is { Parent: Panel panel }) panel.Children.Remove(_overlay);

            _textBox = null;
            _overlay = null;
            _scroll = null;
        }

        private void OnTextChanged(object? sender, TextChangedEventArgs e) => Schedule();

        private void OnTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Ширина окна влияет на переносы — перепроверяем.
            if (e.Property == BoundsProperty) Schedule();
        }

        private static readonly AvaloniaProperty BoundsProperty = Visual.BoundsProperty;

        private void OnScrollChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // При прокрутке текст не меняется — только перерисовываем линии.
            if (e.Property == ScrollViewer.OffsetProperty) Render();
        }

        // Дебаунс: перезапускаем таймер при каждом изменении.
        private void Schedule()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // Проверяет текст и перерисовывает подчёркивания.
        private void Recheck()
        {
            var textBox = _textBox;
            var service = _service;
            if (textBox is null || service is null) return;

            EnsureOverlay();
            FindScrollViewer();

            var text = textBox.Text ?? "";

            // Пока словари грузятся (или текст очень большой) — не подчёркиваем.
            if (service.IsLoaded && text.Length <= 200_000)
            {
                _errors = SpellCheckService.Tokenize(text)
                    .Where(w => !service.IsCorrect(w.Word))
                    .ToList();
            }
            else
            {
                _errors = new List<(int, int, string)>();
            }

            Render();
        }

        // Подложка поверх TextBox: рисуем в ней, клики она не перехватывает.
        private void EnsureOverlay()
        {
            if (_textBox is null || _overlay is not null) return;

            _overlay = new Canvas { IsHitTestVisible = false };

            if (_textBox.Parent is Grid grid)
            {
                Grid.SetRow(_overlay, Grid.GetRow(_textBox));
                Grid.SetColumn(_overlay, Grid.GetColumn(_textBox));
                grid.Children.Add(_overlay);
            }
        }

        // Находит внутренний ScrollViewer TextBox (для сдвига при прокрутке).
        private void FindScrollViewer()
        {
            if (_textBox is null || _scroll is not null) return;

            _scroll = _textBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is not null) _scroll.PropertyChanged += OnScrollChanged;
        }

        // Рисует волнистые линии под словами с ошибками.
        private void Render()
        {
            var textBox = _textBox;
            var overlay = _overlay;
            if (textBox is null || overlay is null) return;

            overlay.Children.Clear();
            if (_errors.Count == 0) return;

            // Берём точную раскладку текста у самого TextBox — координаты совпадут.
            var presenter = textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            if (presenter is null) return;

            var layout = presenter.TextLayout;
            var origin = presenter.TranslatePoint(new Point(0, 0), overlay);
            if (origin is null) return;

            var scroll = _scroll?.Offset ?? default;
            var textLength = textBox.Text?.Length ?? 0;

            foreach (var (start, length, _) in _errors)
            {
                if (start + length > textLength) continue;

                var startRect = layout.HitTestTextPosition(start);
                var endRect = layout.HitTestTextPosition(start + length);

                // Слово перенесено на две строки — пропускаем (редкий случай).
                if (Math.Abs(startRect.Y - endRect.Y) > 0.5) continue;

                var x0 = origin.Value.X + startRect.X - scroll.X;
                var x1 = origin.Value.X + endRect.X - scroll.X;
                var y = origin.Value.Y + startRect.Bottom - scroll.Y + 1.5;

                if (x1 <= x0) continue;

                overlay.Children.Add(CreateZigzag(x0, x1, y));
            }
        }

        // Волнистая линия заданной ширины в точке (x0..x1, y).
        private static Path CreateZigzag(double x0, double x1, double y)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x0, y), isFilled: false);

                var up = true;
                for (var x = x0 + 3; x < x1; x += 5)
                {
                    ctx.LineTo(new Point(x, up ? y - 2.2 : y));
                    up = !up;
                }

                ctx.LineTo(new Point(x1, y));
                ctx.EndFigure(isClosed: false);
            }

            return new Path
            {
                Data = geometry,
                Stroke = UnderlinePen.Brush,
                StrokeThickness = UnderlinePen.Thickness,
                IsHitTestVisible = false,
            };
        }
    }
}
