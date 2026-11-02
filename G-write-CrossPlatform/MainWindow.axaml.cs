using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace G_write
{
    /// <summary>
    /// Главное окно программы управления ЧПУ станком (ASIS-3AX).
    /// Полный перенос логики оригинального Form1.cs с адаптацией под Avalonia UI (.NET 8)
    /// для нативной работы на Windows и Linux без усложненного ООП (стиль прямого Code-Behind).
    /// </summary>
    public partial class MainWindow : Window
    {
        // =========================================================================
        // ПЕРЕМЕННЫЕ И СОСТОЯНИЕ (1:1 ИЗ ОРИГИНАЛЬНОГО FORM1.CS)
        // =========================================================================

        // Флаг служебной команды опроса (подавляет вывод лишних подтверждений "ok" в терминал)
        private bool isCounterCommand = false;

        // Координаты станка (строковые представления для UI)
        string sx = "0.0", sy = "0.0", sz = "0.0";

        // =========================================================================
        // [ИЗМЕНЕНО: КЭШИРОВАНИЕ КООРДИНАТ]
        // БЫЛО: В исходной версии Form1.cs метки label17, label18, label19 обновлялись
        //       каждые 100 мс безусловно, вызывая постоянную перерисовку окна даже
        //       когда станок неподвижен.
        // СТАЛО: Сохраняем предыдущие значения lastSx, lastSy, lastSz и обновляем UI
        //        только тогда, когда станок действительно изменил позицию.
        // =========================================================================
        private string lastSx = "", lastSy = "", lastSz = "";

        // Индекс текущей отправляемой строки G-кода
        int i = 0;

        // Расчетная суммарная длина перемещения инструмента (мм)
        float length = 0;

        // Объект асинхронного ожидания ответа "ok" от станка при построчной передаче G-кода
        private TaskCompletionSource<string>? responseTcs;
        private readonly object tcsLock = new object();

        // Параметры плана сканирования (зигзаг)
        float xStart = 0, yStart = 0, xEnd = 0, yEnd = 0, step = 0;

        // Флаг активного соединения с контроллером
        bool isConnected = false;

        // Списки портов, шагов и скоростей для выпадающих списков
        string[] ports = Array.Empty<string>();
        string[] steps = ["1", "5", "10", "50"];
        string[] speeds = ["10", "20", "50", "100"];

        // Текущий выбранный шаг (мм) и скорость (пересчитанная в F мм/мин)
        string stepHand = "10";
        string speedHand = (int.Parse("10") * 60).ToString();

        // Экземпляр активного соединения (Serial COM-порт или TCP Wi-Fi сокет)
        ICNCConnection? port;

        // Настройки соединения (IP, порт, последний выбранный COM-порт), сохраняются в JSON
        private ConnectionConfig connectionConfig = ConnectionConfig.Load();

        // Флаги управления потоком отправки G-кода
        public bool sendDone = false;
        public bool isNotPaused = true;
        public bool isNotStop = true;
        public bool isStreaming = false;

        // =========================================================================
        // [ИЗМЕНЕНО: ОГРАНИЧЕНИЕ ОБЪЕМА ТЕРМИНАЛА (КОЛЬЦЕВОЙ БУФЕР)]
        // БЫЛО: В исходном коде использовался WinForms box.Text += ...
        //       При многочасовой работе или отправке тысяч строк G-кода это приводило
        //       к утечке памяти и зависанию Win32 RichEdit на несколько секунд.
        // СТАЛО: Ограничение терминала 300 строками. При превышении автоматически
        //        удаляются первые 80 старых строк, освобождая память.
        // =========================================================================
        private const int MaxTerminalLines = 300;
        string[]? dataToSend;

        // Экранная сенсорная клавиатура
        private NumpadWindow? numpadForm;

        // Ссылка на последнее активное текстовое поле (для ввода с Numpad)
        private TextBox? activeTextBox;

        // Путь к внутреннему временному файлу траектории
        string tempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp.txt");

        // Таймер опроса статуса контроллера (запрос "?") каждые 100 мс
        private DispatcherTimer? timer1;

        // =========================================================================
        // КОНСТРУКТОР И ИНИЦИАЛИЗАЦИЯ ОКНА
        // =========================================================================
        public MainWindow()
        {
            InitializeComponent();

            // Динамическая установка версии из AppVersion (Single Source of Truth из G-write.csproj)
            label12.Text = AppVersion.LabelString;

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            this.KeyDown += MainWindow_KeyDown;

            // =====================================================================
            // [ИЗМЕНЕНО: ГЛОБАЛЬНЫЙ ПЕРЕХВАТ ФОКУСА ДЛЯ NUMPAD]
            // БЫЛО В WINFORMS: В конструкторе Form1 рекурсивно обходились Controls,
            //                   назначая GotFocus каждому элементу.
            // ПРОБЛЕМА AVALONIA: В конструкторе визуальное дерево XAML еще не построено,
            //                    поэтому обход возвращал 0 элементов.
            // СТАЛО: Добавлен глобальный обработчик маршрутизируемого события GotFocusEvent
            //        с политикой RoutingStrategies.Bubble на уровне всего окна.
            //        Теперь любое сфокусированное поле гарантированно регистрируется Numpad.
            // =====================================================================
            this.AddHandler(InputElement.GotFocusEvent, OnGlobalGotFocus, RoutingStrategies.Bubble);

            InitializeNumpad();
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // Заполнение выпадающих списков шагов и скоростей
            comboBox2.ItemsSource = steps;
            comboBox3.ItemsSource = speeds;
            comboBox4.ItemsSource = speeds;

            comboBox2.SelectedItem = "10";
            comboBox3.SelectedItem = "20";
            comboBox4.SelectedItem = "20";

            // Принудительное связывание событий клика по полям для мгновенного отклика Numpad
            BindTextBox(startPosX);
            BindTextBox(startPosY);
            BindTextBox(endPosX);
            BindTextBox(endPosY);
            BindTextBox(customBox);
            BindTextBox(txtTcpIp);
            BindTextBox(txtTcpPort);
            BindTextBox(comboBox1.InnerTextBox);
            BindTextBox(comboBox2.InnerTextBox);
            BindTextBox(comboBox3.InnerTextBox);
            BindTextBox(comboBox4.InnerTextBox);

            activeTextBox = startPosX;
            UpdateNumpadTargetDisplay();

            // Восстановление настроек соединения из connection_config.json
            InitConnectionSettings();
        }

        private void BindTextBox(TextBox tb)
        {
            if (tb == null) return;
            tb.AddHandler(InputElement.PointerPressedEvent, (s, args) =>
            {
                activeTextBox = tb;
                UpdateNumpadTargetDisplay();
            }, RoutingStrategies.Tunnel);
        }

        private void OnGlobalGotFocus(object? sender, GotFocusEventArgs e)
        {
            if (e.Source is TextBox tb)
            {
                activeTextBox = tb;
                UpdateNumpadTargetDisplay();
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            AdjustInitialWindowSize();
        }

        private void AdjustInitialWindowSize()
        {
            try
            {
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen == null) return;

                // Коэффициент масштабирования экрана (DPI scaling, например 1.0, 1.25, 1.5)
                double scaling = screen.Scaling;
                if (scaling <= 0) scaling = 1.0;

                // Реальная доступная рабочая область экрана в DIPs (без панели задач)
                double workAreaW = screen.WorkingArea.Width / scaling;
                double workAreaH = screen.WorkingArea.Height / scaling;

                double targetW = 1440;
                double targetH = 860;

                // Задаем безопасный максимальный размер (88% от рабочей области экрана),
                // гарантирующий наличие удобных рамок для перетаскивания и изменения размера
                double maxAllowedW = workAreaW * 0.88;
                double maxAllowedH = workAreaH * 0.88;

                // Если базовый размер не умещается в безопасную зону рабочей области
                if (targetW > maxAllowedW || targetH > maxAllowedH)
                {
                    double scaleFactor = Math.Min(maxAllowedW / targetW, maxAllowedH / targetH);
                    targetW = Math.Max(MinWidth, Math.Round(targetW * scaleFactor));
                    targetH = Math.Max(MinHeight, Math.Round(targetH * scaleFactor));

                    this.Width = targetW;
                    this.Height = targetH;

                    // Центрируем окно в видимой рабочей области
                    double leftDips = (screen.WorkingArea.X / scaling) + Math.Max(0, (workAreaW - targetW) / 2.0);
                    double topDips = (screen.WorkingArea.Y / scaling) + Math.Max(0, (workAreaH - targetH) / 2.0);

                    this.Position = new PixelPoint(
                        (int)Math.Round(leftDips * scaling),
                        (int)Math.Round(topDips * scaling)
                    );
                }
            }
            catch { }
        }

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            try
            {
                isStreaming = false;
                if (isConnected)
                {
                    disconnect();
                }
                if (numpadForm != null)
                {
                    numpadForm.AllowClose = true;
                    numpadForm.Close();
                }
            }
            catch { }
        }



        // =========================================================================
        // ВЫВОД В ТЕРМИНАЛ (БЕЗОПАСНЫЙ ПОТОКОВЫЙ ВЫВОД)
        // =========================================================================
        // [ИЗМЕНЕНО: ПОТОКОБЕЗОПАСНОСТЬ AVALONIA]
        // БЫЛО В WINFORMS: this.InvokeRequired и this.BeginInvoke(new MethodInvoker(...))
        // СТАЛО В AVALONIA: Dispatcher.UIThread.Post(...) для гарантированного выполнения
        //                   кода в UI-потоке на любой операционной системе (Windows/Linux).
        // =========================================================================
        private void AppendLog(string text)
        {
            Dispatcher.UIThread.Post(() =>
            {
                string currentText = box.Text ?? string.Empty;
                var lines = currentText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                // Кольцевой буфер: удаляем первые 80 строк при переполнении 300 строк
                if (lines.Length > MaxTerminalLines)
                {
                    var newLines = new List<string>(lines.Length);
                    for (int k = 80; k < lines.Length; k++)
                    {
                        newLines.Add(lines[k]);
                    }
                    currentText = string.Join(Environment.NewLine, newLines);
                }

                if (string.IsNullOrEmpty(currentText))
                {
                    box.Text = text;
                }
                else
                {
                    box.Text = currentText + Environment.NewLine + text;
                }

                // Автоматическая прокрутка в конец текста
                box.CaretIndex = box.Text.Length;
            });
        }

        // =========================================================================
        // ЭКРАННАЯ КЛАВИАТУРА (NUMPAD)
        // =========================================================================
        private void InitializeNumpad()
        {
            numpadForm = new NumpadWindow();

            // Ввод цифры или точки в текущую позицию курсора
            numpadForm.NumberPressed += (s, num) =>
            {
                if (activeTextBox == null) activeTextBox = startPosX;

                int caret = activeTextBox.CaretIndex;
                string cur = activeTextBox.Text ?? string.Empty;
                if (caret < 0 || caret > cur.Length) caret = cur.Length;

                activeTextBox.Text = cur.Insert(caret, num);
                activeTextBox.CaretIndex = caret + num.Length;
                activeTextBox.Focus();
            };

            // Удаление символа перед курсором (Backspace)
            numpadForm.BackspacePressed += (s, e) =>
            {
                if (activeTextBox == null) activeTextBox = startPosX;

                string cur = activeTextBox.Text ?? string.Empty;
                int caret = activeTextBox.CaretIndex;

                if (caret > 0 && caret <= cur.Length)
                {
                    activeTextBox.Text = cur.Remove(caret - 1, 1);
                    activeTextBox.CaretIndex = caret - 1;
                }
                else if (cur.Length > 0)
                {
                    activeTextBox.Text = cur[..^1];
                    activeTextBox.CaretIndex = activeTextBox.Text.Length;
                }
                activeTextBox.Focus();
            };

            // Очистка содержимого текущего поля
            numpadForm.ClearPressed += (s, e) =>
            {
                if (activeTextBox == null) activeTextBox = startPosX;
                activeTextBox.Clear();
                activeTextBox.Focus();
            };

            // Нажатие "Ввод" (циклический переход между полями точек A и B)
            numpadForm.EnterPressed += (s, e) =>
            {
                if (activeTextBox == startPosX)
                {
                    activeTextBox = startPosY;
                }
                else if (activeTextBox == startPosY)
                {
                    activeTextBox = endPosX;
                }
                else if (activeTextBox == endPosX)
                {
                    activeTextBox = endPosY;
                }
                else if (activeTextBox == endPosY)
                {
                    activeTextBox = startPosX;
                }
                else if (activeTextBox == customBox)
                {
                    Button4_Click(null, null);
                }

                activeTextBox?.Focus();
                UpdateNumpadTargetDisplay();
            };
        }

        /// <summary>
        /// Обновляет надпись в окне Numpad, показывающую оператору активное поле ввода.
        /// </summary>
        private void UpdateNumpadTargetDisplay()
        {
            if (numpadForm == null) return;

            string name = "Точка A (X)";
            if (activeTextBox == startPosX) name = "Точка A (X)";
            else if (activeTextBox == startPosY) name = "Точка A (Y)";
            else if (activeTextBox == endPosX) name = "Точка B (X)";
            else if (activeTextBox == endPosY) name = "Точка B (Y)";
            else if (activeTextBox == customBox) name = "G-код консоль";
            else if (activeTextBox == txtTcpIp) name = "IP адрес";
            else if (activeTextBox == txtTcpPort) name = "TCP порт";
            else if (activeTextBox == comboBox2.InnerTextBox) name = "Шаг (мм)";
            else if (activeTextBox == comboBox3.InnerTextBox) name = "Скорость джога";
            else if (activeTextBox == comboBox4.InnerTextBox) name = "Скорость (мм/с)";
            else if (activeTextBox == comboBox1.InnerTextBox) name = "COM-порт";

            numpadForm.SetTargetName(name);
        }

        private void ToggleNumpadButton_Click(object? sender, RoutedEventArgs e)
        {
            if (numpadForm == null) return;

            if (numpadForm.IsVisible)
            {
                numpadForm.Hide();
            }
            else
            {
                // Позиционируем окно клавиатуры справа от основного окна
                var screenBounds = this.Position;
                numpadForm.Position = new PixelPoint(screenBounds.X + (int)this.Bounds.Width + 10, screenBounds.Y);
                numpadForm.Show();

                if (activeTextBox == null) activeTextBox = startPosX;
                UpdateNumpadTargetDisplay();
                activeTextBox.Focus();
            }
        }

        // =========================================================================
        // УПРАВЛЕНИЕ СТРЕЛКАМИ КЛАВИАТУРЫ (JOG HOTKEYS)
        // =========================================================================
        // [ИЗМЕНЕНО: ПЕРЕХВАТ КЛАВИАТУРЫ В AVALONIA]
        // БЫЛО В WINFORMS: Переопределение ProcessDialogKey(Keys keyData)
        // СТАЛО В AVALONIA: Обработчик Window.KeyDown. Если пользователь не вводит
        //                   команду в поле customBox, стрелки вызывают перемещение осей.
        // =========================================================================
        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is TextBox && e.Source == customBox) return;

            if (e.Key == Key.Left)
            {
                buttonLeftXY_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                buttonRightXY_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                buttonUpXY_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                buttonDownXY_Click(null, null);
                e.Handled = true;
            }
        }

        // =========================================================================
        // НАСТРОЙКА И УПРАВЛЕНИЕ ПОДКЛЮЧЕНИЕМ (COM / TCP WI-FI)
        // =========================================================================
        private void InitConnectionSettings()
        {
            txtTcpIp.Text = connectionConfig.TcpHost;
            txtTcpPort.Text = connectionConfig.TcpPort.ToString();

            if (connectionConfig.IsTcpMode)
            {
                rbTcp.IsChecked = true;
                rbSerial.IsChecked = false;
            }
            else
            {
                rbSerial.IsChecked = true;
                rbTcp.IsChecked = false;
            }

            RefreshComPorts();
            SwitchConnectionMode(connectionConfig.IsTcpMode);
        }

        private void rbSerial_CheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (rbSerial.IsChecked == true)
            {
                SwitchConnectionMode(false);
            }
        }

        private void rbTcp_CheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (rbTcp.IsChecked == true)
            {
                SwitchConnectionMode(true);
            }
        }

        private void SwitchConnectionMode(bool isTcp)
        {
            connectionConfig.IsTcpMode = isTcp;
            connectionConfig.Save();

            pnlSerial.IsVisible = !isTcp;
            pnlTcp.IsVisible = isTcp;
        }

        /// <summary>
        /// Сканирует последовательные порты в системе (на Windows COMx, на Linux /dev/ttyUSB* и /dev/ttyACM*).
        /// </summary>
        private void RefreshComPorts()
        {
            try
            {
                ports = SerialCNCConnection.GetAvailablePorts();
                comboBox1.ItemsSource = null;
                comboBox1.ItemsSource = ports;

                if (!string.IsNullOrEmpty(connectionConfig.LastComPort) && Array.IndexOf(ports, connectionConfig.LastComPort) >= 0)
                {
                    comboBox1.SelectedItem = connectionConfig.LastComPort;
                }
                else if (ports.Length > 0)
                {
                    comboBox1.SelectedIndex = 0;
                }
            }
            catch { }
        }

        private void button2_Click(object? sender, RoutedEventArgs e)
        {
            if (!isConnected)
            {
                RefreshComPorts();
            }
        }

        private void button1_Click(object? sender, RoutedEventArgs e)
        {
            if (!isConnected)
            {
                connect();
            }
            else
            {
                disconnect();
            }
        }

        /// <summary>
        /// Устанавливает соединение с ЧПУ контроллером по выбранному каналу.
        /// </summary>
        private void connect()
        {
            if (rbTcp.IsChecked == true)
            {
                // Подключение по Wi-Fi (TCP сокет к ESP-32S)
                string host = (txtTcpIp.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(host))
                {
                    AppendLog("[Ошибка] Введите IP адрес ESP-32S (например 192.168.4.1)");
                    return;
                }
                if (!int.TryParse((txtTcpPort.Text ?? string.Empty).Trim(), out int tcpPort) || tcpPort <= 0 || tcpPort > 65535)
                {
                    AppendLog("[Ошибка] Укажите корректный номер TCP порта (по умолчанию 8088)");
                    return;
                }

                connectionConfig.TcpHost = host;
                connectionConfig.TcpPort = tcpPort;
                connectionConfig.IsTcpMode = true;
                connectionConfig.Save();

                lblConnStatus.Text = "Подключение...";
                lblConnStatus.Foreground = Brushes.OrangeRed;

                try
                {
                    var tcpConn = new TcpCNCConnection(host, tcpPort);
                    tcpConn.LineReceived += OnLineReceived;
                    tcpConn.Disconnected += OnConnectionLost;
                    port = tcpConn;
                    port.Open();

                    lblConnStatus.Text = $"TCP: {host}:{tcpPort}";
                    lblConnStatus.Foreground = Brushes.DarkGreen;
                }
                catch (Exception ex)
                {
                    lblConnStatus.Text = "Ошибка связи";
                    lblConnStatus.Foreground = Brushes.Red;
                    AppendLog($"[Ошибка] Не удалось подключиться к ESP-32S по TCP: {ex.Message}");
                    return;
                }
            }
            else
            {
                // Подключение по USB Serial COM-порту
                string? selectedPort = comboBox1.Text?.Trim();
                if (string.IsNullOrEmpty(selectedPort))
                {
                    AppendLog("[Ошибка] Выберите нужный COM-порт");
                    return;
                }

                connectionConfig.LastComPort = selectedPort;
                connectionConfig.IsTcpMode = false;
                connectionConfig.Save();

                lblConnStatus.Text = "Подключение...";
                lblConnStatus.Foreground = Brushes.OrangeRed;

                try
                {
                    var serialConn = new SerialCNCConnection(selectedPort, 115200);
                    serialConn.LineReceived += OnLineReceived;
                    serialConn.Disconnected += OnConnectionLost;
                    port = serialConn;
                    port.Open();

                    lblConnStatus.Text = $"COM: {selectedPort}";
                    lblConnStatus.Foreground = Brushes.DarkGreen;
                }
                catch (Exception ex)
                {
                    lblConnStatus.Text = "Ошибка порта";
                    lblConnStatus.Foreground = Brushes.Red;
                    AppendLog($"[Ошибка] Ошибка открытия порта: {ex.Message}");
                    return;
                }
            }

            try
            {
                InitTimer();
                button1.Content = "Отключить";
                isConnected = true;
                rbSerial.IsEnabled = false;
                rbTcp.IsEnabled = false;
                txtTcpIp.IsEnabled = false;
                txtTcpPort.IsEnabled = false;
                comboBox1.IsEnabled = false;
                button2.IsEnabled = false;
                AppendLog("[Система] Соединение успешно установлено");
            }
            catch (Exception ex)
            {
                AppendLog($"[Ошибка] Ошибка инициализации: {ex.Message}");
            }
        }

        private void disconnect()
        {
            isConnected = false;

            try
            {
                timer1?.Stop();
                port?.Close();
                port?.Dispose();
                port = null;
            }
            catch { }

            button1.Content = "Подключить";
            rbSerial.IsEnabled = true;
            rbTcp.IsEnabled = true;
            txtTcpIp.IsEnabled = true;
            txtTcpPort.IsEnabled = true;
            comboBox1.IsEnabled = true;
            button2.IsEnabled = true;
            lblConnStatus.Text = "Отключено";
            lblConnStatus.Foreground = Brushes.DimGray;
            AppendLog("[Система] Связь отключена");
        }

        private void OnConnectionLost(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (isConnected)
                {
                    disconnect();
                    AppendLog("[Предупреждение] Связь с контроллером разорвана!");
                }
            });
        }

        // =========================================================================
        // ПАРСИНГ ОТВЕТОВ СТАНКА (WPos) И ОБРАБОТКА ПОТОКА
        // =========================================================================
        // [ИЗМЕНЕНО: БЫСТРЫЙ РАЗБОР БЕЗ РЕГУЛЯРНЫХ ВЫРАЖЕНИЙ]
        // БЫЛО В WINFORMS: Regex.Match(line, @"WPos:([-0-9.]+),([-0-9.]+),([-0-9.]+)")
        // ПРОБЛЕМА: При опросе каждые 100 мс создание Regex выделяло мегабайты мусора
        //           и нагружало GC на слабых одноплатных ПК (Raspberry Pi).
        // СТАЛО: Быстрый поиск через IndexOf("WPos:") и split по запятым. Работает мгновенно
        //        без создания временных объектов.
        // =========================================================================
        private void OnLineReceived(object? sender, string line)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) return;

            if (line.StartsWith("<") && line.Contains("WPos:"))
            {
                try
                {
                    int wposIdx = line.IndexOf("WPos:");
                    if (wposIdx >= 0)
                    {
                        int start = wposIdx + 5;
                        int end = line.IndexOfAny(new char[] { '|', '>' }, start);
                        if (end > start)
                        {
                            string[] coords = line.Substring(start, end - start).Split(',');
                            if (coords.Length >= 3)
                            {
                                if (double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double dx) &&
                                    double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dy) &&
                                    double.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dz))
                                {
                                    sx = dx.ToString("F1", CultureInfo.InvariantCulture);
                                    sy = dy.ToString("F1", CultureInfo.InvariantCulture);
                                    sz = dz.ToString("F1", CultureInfo.InvariantCulture);

                                    // Обновляем метки в интерфейсе ТОЛЬКО при реальном изменении позиции
                                    if (sx != lastSx || sy != lastSy || sz != lastSz)
                                    {
                                        lastSx = sx; lastSy = sy; lastSz = sz;
                                        Dispatcher.UIThread.Post(() =>
                                        {
                                            label17.Text = $"X: {sx}";
                                            label18.Text = $"Y: {sy}";
                                            label19.Text = $"Z: {sz}";
                                        });
                                    }
                                }
                            }
                        }
                    }
                    return;
                }
                catch { }
            }

            // Игнорируем эхо команды опроса
            if (isCounterCommand && line == "ok")
            {
                isCounterCommand = false;
                return;
            }

            AppendLog(line);

            // Сигнализируем ожидающему потоку стримера о получении ответа "ok"
            lock (tcsLock)
            {
                if (responseTcs != null && !responseTcs.Task.IsCompleted)
                {
                    if (line.Length >= 2 && line.StartsWith("ok"))
                    {
                        responseTcs.SetResult(line);
                    }
                }
            }
        }

        // =========================================================================
        // ТАЙМЕР ОПРОСА СТАТУСА (100 мс)
        // =========================================================================
        public void InitTimer()
        {
            if (timer1 == null)
            {
                timer1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer1.Tick += Timer1_Tick;
            }
            timer1.Start();
        }

        private void Timer1_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (port != null && port.IsOpen)
                {
                    isCounterCommand = true;
                    port.WriteLine("?"); // Стандартная GRBL/Inectra команда запроса статуса
                }
            }
            catch { }
        }

        // =========================================================================
        // ГЕНЕРАТОР G-КОДА (ЗИГЗАГ-ПЛАН СКАНИРОВАНИЯ)
        // =========================================================================
        private float ParseFloatSafe(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0f;
            input = input.Trim().Replace(',', '.');
            if (float.TryParse(input, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out float res))
                return res;
            return 0f;
        }

        /// <summary>
        /// Сохраняет метаданные координат сканирования в шапку файла G-кода в виде комментариев.
        /// </summary>
        private void SaveCoordinates(string file, double[] coords)
        {
            try
            {
                string header = $"; x1={coords[0]}\n; y1={coords[1]}\n; x2={coords[2]}\n; y2={coords[3]}\n; spd={coords[4]}\n; stp={coords[5]}\n; time={coords[6]}\n\n";
                string tempCopy = file + ".tmp";
                using (var writer = new StreamWriter(tempCopy))
                {
                    writer.Write(header);
                    if (File.Exists(file))
                    {
                        using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read))
                        {
                            reader.CopyTo(writer.BaseStream);
                        }
                    }
                }
                File.Move(tempCopy, file, true);
            }
            catch { }
        }

        /// <summary>
        /// Читает метаданные координат из шапки G-кода для автозаполнения полей при открытии файла.
        /// </summary>
        private double[] LoadCoordinates(string file)
        {
            double[] coords = new double[7];
            if (!File.Exists(file)) return coords;
            int k = 0;

            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith(";") && line.Contains("=") && k < 7)
                {
                    string[] split = line.Split('=');
                    if (split.Length > 1)
                    {
                        double.TryParse(split[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out coords[k++]);
                    }
                }
                else if (!line.StartsWith(";"))
                {
                    break;
                }
            }
            return coords;
        }

        /// <summary>
        /// Расчет и формирование зигзагообразной траектории перемещения инструмента.
        /// </summary>
        public void gen()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(startPosX.Text) || string.IsNullOrWhiteSpace(startPosY.Text))
                {
                    AppendLog("[Ошибка] Поле координат не может быть пустым!");
                    return;
                }

                xStart = ParseFloatSafe(startPosX.Text);
                yStart = ParseFloatSafe(startPosY.Text);
                xEnd = ParseFloatSafe(endPosX.Text);
                yEnd = ParseFloatSafe(endPosY.Text);
                step = ParseFloatSafe(comboBox2.Text);

                if (step <= 0)
                {
                    AppendLog("[Ошибка] Шаг должен быть больше нуля!");
                    return;
                }
            }
            catch
            {
                AppendLog("[Ошибка] Заполните все поля плана сканирования!");
                return;
            }

            float Xl = Math.Abs(xEnd - xStart);
            float Yl = yEnd - yStart;
            int k = (int)(Yl / step);

            int spdVal = int.TryParse(comboBox4.Text, out int sVal) ? sVal : 20;
            int speed = spdVal * 60;

            using (StreamWriter outline = new StreamWriter(tempPath))
            {
                outline.WriteLine("G90");
                outline.WriteLine("G17");
                outline.WriteLine("G01 X" + xStart.ToString(CultureInfo.InvariantCulture) + " Y" + yStart.ToString(CultureInfo.InvariantCulture) + " F" + speed);

                if (k == 0)
                {
                    outline.WriteLine("G01 X" + xEnd.ToString(CultureInfo.InvariantCulture) + " F" + speed);
                    length += Xl;
                }
                else
                {
                    float currentY = yStart;
                    for (int m = 0; m <= k; m++)
                    {
                        if (m % 2 == 0)
                        {
                            outline.WriteLine("G01 X" + xEnd.ToString(CultureInfo.InvariantCulture) + " F" + speed);
                        }
                        else
                        {
                            outline.WriteLine("G01 X" + xStart.ToString(CultureInfo.InvariantCulture) + " F" + speed);
                        }

                        if (m < k)
                        {
                            if (m == k - 1)
                            {
                                currentY = yEnd;
                            }
                            else
                            {
                                currentY = yStart + (m + 1) * step;
                            }
                            outline.WriteLine("G01 Y" + currentY.ToString(CultureInfo.InvariantCulture) + " F" + speed);
                        }
                    }
                    length += (k + 1) * Xl + Yl;
                }
            }

            double calcTime = spdVal > 0 ? (length / spdVal) : 0;
            label16.Text = ((int)calcTime).ToString();

            SaveCoordinates(tempPath, new double[] { xStart, yStart, xEnd, yEnd, spdVal, step, calcTime });
            length = 0;
            AppendLog("[Успех] Траектория сканирования сгенерирована.");
        }

        private void genButton_Click(object? sender, RoutedEventArgs e) => gen();

        private void button6_Click(object? sender, RoutedEventArgs e)
        {
            startPosX.Text = sx;
            startPosY.Text = sy;
        }

        private void button7_Click(object? sender, RoutedEventArgs e)
        {
            endPosX.Text = sx;
            endPosY.Text = sy;
        }

        // =========================================================================
        // ПОТОКОВАЯ ПЕРЕДАЧА G-КОДА (СТРИМИНГ)
        // =========================================================================
        // [ИЗМЕНЕНО: ОТКАЗ ОТ РЕКУРСИИ В ПОЛЬЗУ ЛИНЕЙНОГО ЦИКЛА]
        // БЫЛО В WINFORMS: Рекурсивный метод SendDataAndWaitForResponse(line) вызывал
        //                   сам себя на каждом кадре, порождая тысячи вложенных Task в куче.
        //                   При потере ответа выполнение зависало навсегда.
        // СТАЛО: Плоский линейный цикл for с контролем отмены (isStreaming), паузы (isNotPaused)
        //        и жестким таймаутом ожидания 5 секунд на каждый кадр.
        // =========================================================================
        private async Task StreamGCodeAsync()
        {
            if (port == null || !port.IsOpen)
            {
                AppendLog("[Ошибка] Порт не открыт!");
                return;
            }

            if (!File.Exists(tempPath))
            {
                AppendLog("[Ошибка] Файл G-кода не найден! Сначала нажмите 'Генерировать'.");
                return;
            }

            dataToSend = File.ReadAllLines(tempPath);
            if (dataToSend.Length == 0)
            {
                AppendLog("[Ошибка] Файл G-кода пуст!");
                return;
            }

            isStreaming = true;
            isNotPaused = true;
            sendDone = false;
            progressBar1.Minimum = 0;
            progressBar1.Maximum = dataToSend.Length;

            for (; i < dataToSend.Length; i++)
            {
                if (!isStreaming)
                {
                    AppendLog("[Отправка остановлена]");
                    break;
                }

                // Ожидание снятия с паузы
                while (!isNotPaused && isStreaming)
                {
                    await Task.Delay(100);
                }
                if (!isStreaming) break;

                string line = dataToSend[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";"))
                {
                    progressBar1.Value = Math.Min(i + 1, progressBar1.Maximum);
                    continue;
                }

                lock (tcsLock)
                {
                    responseTcs = new TaskCompletionSource<string>();
                }
                isCounterCommand = false;
                port.WriteLine(line);
                AppendLog(">> " + line);

                // Защита от зависания: таймаут 5 секунд
                var completedTask = await Task.WhenAny(responseTcs.Task, Task.Delay(5000));
                if (completedTask != responseTcs.Task)
                {
                    AppendLog($"[Предупреждение] Таймаут ответа от станка на: {line}");
                }

                progressBar1.Value = Math.Min(i + 1, progressBar1.Maximum);
                label10.Text = "Отправка" + new string('.', (i % 4) + 1);
            }

            if (i >= dataToSend.Length)
            {
                label10.Text = "Завершено";
                AppendLog("[Выполнение G-кода успешно завершено]");
                sendDone = true;
                i = 0;
            }

            isStreaming = false;
        }

        private async void button3_Click(object? sender, RoutedEventArgs e)
        {
            await StreamGCodeAsync();
        }

        // =========================================================================
        // ПАУЗА, СТОП, СБРОС И ВОЗВРАТ В НАЧАЛО
        // =========================================================================
        // [ИЗМЕНЕНО: БЕЗОПАСНАЯ ОСТАНОВКА ТАЙМЕРА]
        // БЫЛО В WINFORMS: timer1.Dispose(), что приводило к вылету ObjectDisposedException
        //                   при попытке возобновления работы.
        // СТАЛО: Корректный вызов timer1.Stop() и timer1.Start().
        // =========================================================================
        private void button11_Click(object? sender, RoutedEventArgs e)
        {
            if (port == null || !port.IsOpen) return;

            if (isNotPaused)
            {
                isNotPaused = false;
                timer1?.Stop();
                txtButton11.Text = "Продолжить";
                try
                {
                    imgButton11.Source = new Bitmap(AssetLoader.Open(new Uri("avares://G-write/Assets/icons8-play-26.png")));
                }
                catch { }

                // Байт 0x82: команда GRBL Feed Hold (приостановка подачи)
                byte[] data = new byte[] { 0x82 };
                port.Write(data, 0, data.Length);
                AppendLog("[Пауза]");
            }
            else
            {
                isNotPaused = true;
                port.Write("~"); // Символ '~': команда GRBL Cycle Start (возобновление движения)
                isCounterCommand = false;
                timer1?.Start();
                txtButton11.Text = "Пауза";
                try
                {
                    imgButton11.Source = new Bitmap(AssetLoader.Open(new Uri("avares://G-write/Assets/btn_button11.png")));
                }
                catch { }

                AppendLog("[Возобновление]");
            }
        }

        private void button10_Click_1(object? sender, RoutedEventArgs e)
        {
            if (port == null || !port.IsOpen) return;

            if (isNotStop)
            {
                isNotStop = false;
                isStreaming = false;
                txtButton10.Text = "РАЗБЛОК.";
                txtIcon10.Text = "🔓";
                timer1?.Stop();

                // Байт 0x18 (Ctrl-X): команда GRBL Soft Reset (экстренный сброс)
                byte[] data = new byte[] { 0x18 };
                port.Write(data, 0, data.Length);
                isCounterCommand = false;
                AppendLog("[АВАРИЙНЫЙ СТОП]");
            }
            else
            {
                txtButton10.Text = "СТОП";
                txtIcon10.Text = "🛑";
                port.Write("$X\n"); // Команда разблокировки станка после аварийного останова
                isCounterCommand = false;
                timer1?.Start();
                isNotStop = true;
                AppendLog("[Контроллер разблокирован]");
            }
        }

        private void button12_Click(object? sender, RoutedEventArgs e)
        {
            if (port == null || !port.IsOpen) return;

            string spdStr = comboBox4.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(spdStr) && int.TryParse(spdStr, out int spdInt))
            {
                int spd = spdInt * 60;
                string xPos = (startPosX.Text ?? "0").Replace(",", ".");
                string yPos = (startPosY.Text ?? "0").Replace(",", ".");

                port.Write("G90\n");
                port.Write($"G01 Y {yPos} F{spd}\n");
                port.Write($"G01 X {xPos} F{spd}\n");
                isCounterCommand = false;

                AppendLog("G90");
                AppendLog($"G01 Y {yPos} F{spd}");
                AppendLog($"G01 X {xPos} F{spd}");
            }
            else
            {
                AppendLog("[Ошибка] Укажите скорость!");
            }
        }

        private void button14_Click(object? sender, RoutedEventArgs e)
        {
            if (port == null || !port.IsOpen) return;
            try
            {
                // Макрос программного обнуления координат рабочей системы (G10 L20 P1)
                port.Write("G91 Z 0.1\n");
                port.Write("G91 Z -0.1\n");
                port.Write("G28.1 X0 Y0 Z0\n");
                port.Write("G10 L20 P1 X0 Y0 Z0\n");
                isCounterCommand = false;

                AppendLog("G91 Z 0.1");
                AppendLog("G91 Z -0.1");
                AppendLog("G28.1 X0 Y0 Z0");
                AppendLog("G10 L20 P1 X0 Y0 Z0 (Обнуление осей)");
            }
            catch { }
        }

        private void button15_Click_1(object? sender, RoutedEventArgs e)
        {
            if (port == null || !port.IsOpen) return;

            if (isNotStop)
            {
                try
                {
                    isCounterCommand = false;
                    port.Write("$H\n"); // Команда аппаратного поиска баз по концевикам
                    AppendLog("$H (Поиск базы по концевикам)");
                }
                catch { }
                isNotStop = false;
                button15.Content = "Конец поиска";
                timer1?.Stop();
            }
            else
            {
                button15.Content = "Найти ноль";
                port.Write("$X\n");
                isCounterCommand = false;
                timer1?.Start();
                isNotStop = true;
                AppendLog("$X (Разблокировка)");
            }
        }

        // =========================================================================
        // ДЖОГ (РУЧНОЕ ПЕРЕМЕЩЕНИЕ ОСЕЙ)
        // =========================================================================
        private void radioButton1_CheckedChanged(object? sender, RoutedEventArgs e) => stepHand = "0.1";
        private void radioButton2_CheckedChanged(object? sender, RoutedEventArgs e) => stepHand = "1";
        private void radioButton3_CheckedChanged(object? sender, RoutedEventArgs e) => stepHand = "10";
        private void radioButton4_CheckedChanged(object? sender, RoutedEventArgs e) => stepHand = "5";
        private void radioButton5_CheckedChanged(object? sender, RoutedEventArgs e) => stepHand = "50";
        private void radioButton6_CheckedChanged(object? sender, RoutedEventArgs e) => stepHand = "100";

        private void comboBox3_SelectedIndexChanged(object? sender, EventArgs e)
        {
            string spd = comboBox3.Text.Trim();
            if (int.TryParse(spd, out int s))
            {
                speedHand = (s * 60).ToString();
            }
        }

        public void WriteIt(string toWrite, string prefix)
        {
            if (port == null || !port.IsOpen) return;
            try
            {
                string spd = comboBox3.Text.Trim();
                if (int.TryParse(spd, out int s))
                {
                    speedHand = (s * 60).ToString();
                }

                port.Write(prefix + "\n");
                isCounterCommand = false;
                port.Write(toWrite + "\n");
                isCounterCommand = false;

                AppendLog(toWrite);
            }
            catch { }
        }

        private void buttonUpXY_Click(object? sender, RoutedEventArgs? e)
        {
            // В оригинале инверсия Y для координатной системы станка
            if (port == null || !port.IsOpen) return;
            try
            {
                string spd = comboBox3.Text.Trim();
                if (int.TryParse(spd, out int s))
                {
                    speedHand = (s * 60).ToString();
                }

                port.Write("G91\n");
                port.Write("G01 Y -" + stepHand + " F" + speedHand + "\n");
                isCounterCommand = false;

                AppendLog("G01 Y -" + stepHand + " F" + speedHand);
            }
            catch { }
        }

        private void buttonDownXY_Click(object? sender, RoutedEventArgs? e)
        {
            WriteIt("G01 Y " + stepHand + " F" + speedHand, "G91");
        }

        private void buttonLeftXY_Click(object? sender, RoutedEventArgs? e)
        {
            WriteIt("G01 X -" + stepHand + " F" + speedHand, "G91");
        }

        private void buttonRightXY_Click(object? sender, RoutedEventArgs? e)
        {
            WriteIt("G01 X " + stepHand + " F" + speedHand, "G91");
        }

        private void buttonUpZ_Click(object? sender, RoutedEventArgs? e)
        {
            WriteIt("G01 Z " + stepHand + " F" + speedHand, "G91");
        }

        private void buttonDownZ_Click(object? sender, RoutedEventArgs? e)
        {
            WriteIt("G01 Z -" + stepHand + " F" + speedHand, "G91");
        }

        private void buttonHomeXY_Click(object? sender, RoutedEventArgs? e)
        {
            if (port == null || !port.IsOpen) return;
            try
            {
                port.Write("G28 X0 Y0\n");
                isCounterCommand = false;
                AppendLog("G28 X0 Y0");
            }
            catch { }
        }

        private void buttonHomeZ_Click(object? sender, RoutedEventArgs? e)
        {
            if (port == null || !port.IsOpen) return;
            try
            {
                port.Write("G28 Z0\n");
                isCounterCommand = false;
                AppendLog("G28 Z0");
            }
            catch { }
        }

        // =========================================================================
        // КОНСОЛЬ РУЧНОГО ВВОДА И ТЕРМИНАЛ
        // =========================================================================
        private void customBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                Button4_Click(sender, null);
            }
        }

        private void Button4_Click(object? sender, RoutedEventArgs? e)
        {
            if (port == null || !port.IsOpen)
            {
                AppendLog("[Ошибка] Порт закрыт");
                return;
            }

            string text = (customBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return;

            // Защита: если оператор запрашивает параметры $$ станка, отключаем таймер,
            // чтобы эхо регулярного опроса ? не перемешивалось с выводом настроек EEPROM.
            if (text.Length >= 2 && text.StartsWith("$$"))
            {
                timer1?.Stop();
                AppendLog("[Внимание] Для чтения настроек контроллера таймер опроса временно остановлен.");
            }

            port.Write(text + "\n");
            AppendLog(">> " + text);
            customBox.Clear();
        }

        private void button5_Click(object? sender, RoutedEventArgs e)
        {
            box.Clear();
        }

        // =========================================================================
        // ФАЙЛОВЫЕ ДИАЛОГИ (КРОССПЛАТФОРМЕННЫЕ ЧЕРЕЗ StorageProvider)
        // =========================================================================
        // [ИЗМЕНЕНО: КРОССПЛАТФОРМЕННЫЙ ВЫБОР ФАЙЛОВ В AVALONIA]
        // БЫЛО В WINFORMS: SaveFileDialog / OpenFileDialog жестко завязаны на Win32 API.
        // СТАЛО: Avalonia StorageProvider, который открывает нативный диалог операционной
        //        системы (на Windows диалог Explorer, на Linux диалог GTK/XDG).
        // =========================================================================
        private async void button16_Click(object? sender, RoutedEventArgs e)
        {
            if (!File.Exists(tempPath))
            {
                AppendLog("[Ошибка] Сначала сгенерируйте код. Внутренний файл отсутствует.");
                return;
            }

            string content = File.ReadAllText(tempPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                AppendLog("[Ошибка] Внутренний файл G-кода пуст!");
                return;
            }

            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Сохранить G-code файл",
                    SuggestedFileName = "output.gcode",
                    DefaultExtension = "gcode",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("G-code файлы (*.gcode)") { Patterns = new[] { "*.gcode" } },
                        new FilePickerFileType("Все файлы (*.*)") { Patterns = new[] { "*.*" } }
                    }
                });

                if (file != null)
                {
                    await using var stream = await file.OpenWriteAsync();
                    await using var writer = new StreamWriter(stream, Encoding.UTF8);
                    await writer.WriteAsync(content);
                    AppendLog($"[Успех] Файл сохранён: {file.Name}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Ошибка] Не удалось сохранить файл: {ex.Message}");
            }
        }

        private async void button17_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Открыть G-code файл",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("G-code файлы (*.gcode)") { Patterns = new[] { "*.gcode" } },
                        new FilePickerFileType("Все файлы (*.*)") { Patterns = new[] { "*.*" } }
                    }
                });

                if (files.Count > 0)
                {
                    var file = files[0];
                    await using var stream = await file.OpenReadAsync();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string content = await reader.ReadToEndAsync();

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        AppendLog("[Ошибка] Выбранный G-code файл пуст!");
                        return;
                    }

                    File.WriteAllText(tempPath, content);
                    double[] loaded = LoadCoordinates(tempPath);

                    startPosX.Text = loaded[0].ToString(CultureInfo.InvariantCulture);
                    startPosY.Text = loaded[1].ToString(CultureInfo.InvariantCulture);
                    endPosX.Text = loaded[2].ToString(CultureInfo.InvariantCulture);
                    endPosY.Text = loaded[3].ToString(CultureInfo.InvariantCulture);

                    string spdStr = ((int)loaded[4]).ToString();
                    comboBox4.Text = spdStr;

                    string stpStr = ((int)loaded[5]).ToString();
                    comboBox2.Text = stpStr;

                    label16.Text = ((int)loaded[6]).ToString();
                    AppendLog($"[Успех] Загружен файл G-кода: {file.Name}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Ошибка] Не удалось открыть файл: {ex.Message}");
            }
        }

        // =========================================================================
        // ПРОЗРАЧНОСТЬ ОКНА И ДИАЛОГИ
        // =========================================================================
        private void trackBar1_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (lblOpacity != null)
            {
                int val = (int)e.NewValue;
                this.Opacity = val / 100.0;
                lblOpacity.Text = $"{val}%";
            }
        }

        private void button9_Click_2(object? sender, RoutedEventArgs e)
        {
            var about = new AboutWindow();
            about.ShowDialog(this);
        }

        private void button8_ClickAsync(object? sender, RoutedEventArgs e)
        {
            var update = new UpdateWindow();
            update.ShowDialog(this);
        }
    }
}
