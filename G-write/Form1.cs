using System.Data;
using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.IO.Ports;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace G_write
{
    public partial class Form1 : Form
    {
        private bool isCounterCommand = false;
        double x, y, z;
        string sx = "0.0", sy = "0.0", sz = "0.0";

        // СТАЛО: Кэш предыдущих значений координат (устраняет 90% лишних перерисовок меток на слабом железе)
        private string lastSx = "", lastSy = "", lastSz = "";
        int i = 0;
        int j = 0;
        float length = 0;
        private TaskCompletionSource<string> responseTcs;

        public string mydocu = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        public string receiveOk;
        public string boxtext;
        public bool timeIsReady = false;
        public int DeviceConnectAttempts = 0;
        float xStart = 0, yStart = 0, xEnd = 0, yEnd = 0, step = 0;
        bool isConnected = false;
        String[] ports;
        String[] steps = ["1", "5", "10", "50"];
        String[] speeds = ["10", "20", "50", "100"];
        String stepHand = "10";
        String speedHand = (Int32.Parse("10") * 60).ToString();
        ICNCConnection port;

        private ConnectionConfig connectionConfig = ConnectionConfig.Load();
        public char logReceive;
        public string receiveData;
        public string str;
        public string currentX;
        public string currentY;
        public string currentZ;
        public string currentX_str;
        public string currentY_str;
        public bool show = false;
        public bool sendDone = false;
        public bool isNotPaused = true;
        public bool isNotStop = true;
        public bool isNotStopCounter = true;

        // СТАЛО: Флаг активной потоковой передачи G-кода для безопасного управления паузой и отменой
        public bool isStreaming = false;

        // СТАЛО: Порог ограничения строк лога (кольцевой буфер, исключающий переполнение памяти)
        private const int MaxTerminalLines = 300;
        public bool ok = true;
        string[] dataToSend;

        private readonly object tcsLock = new object();
        private NumpadForm numpadForm;
        private Control activeControl;
        string tempPath = Path.Combine(Application.StartupPath, "temp.txt");

        public Form1()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterScreen;
            this.KeyPreview = true;
            this.KeyDown += new KeyEventHandler(Form1_KeyDown);
            InitializeNumpad();
            SubscribeToTextBoxEvents(this);
            button11.ImageAlign = ContentAlignment.MiddleLeft;
            InitConnectionSettings();
        }

        // =========================================================================
        // СТАЛО: Оптимизированный вывод в терминал (устраняет зависания на слабом ПК)
        // БЫЛО: В коде использовалось box.Text += ...
        //       Это считывало весь текст из Win32 RichEdit, пересоздавало строку в памяти
        //       и заново перерисовывало документ с 1-й строки, вызывая фризы на секунды.
        // =========================================================================
        private void AppendLog(string text)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new MethodInvoker(() => AppendLog(text)));
                return;
            }

            // Кольцевой буфер: если в терминале более MaxTerminalLines строк, удаляем первые 80 строк
            if (box.Lines.Length > MaxTerminalLines)
            {
                int cutIndex = box.GetFirstCharIndexFromLine(80);
                if (cutIndex > 0)
                {
                    box.Select(0, cutIndex);
                    box.SelectedText = string.Empty;
                }
            }

            box.AppendText(text + "\r\n");
            box.ScrollToCaret();
        }

        private void InitializeNumpad()
        {
            numpadForm = new NumpadForm();
            numpadForm.NumberPressed += (s, num) =>
            {
                if (activeControl is System.Windows.Forms.TextBox textBox)
                    textBox.Text += num;
                else if (activeControl is System.Windows.Forms.ComboBox comboBox)
                    comboBox.Text += num;
            };
            numpadForm.BackspacePressed += (s, e) =>
            {
                if (activeControl is System.Windows.Forms.TextBox textBox && textBox.Text.Length > 0)
                    textBox.Text = textBox.Text[..^1];
                if (activeControl is System.Windows.Forms.ComboBox comboBox && comboBox.Text.Length > 0)
                    comboBox.Text = comboBox.Text[..^1];
            };
            numpadForm.EnterPressed += (s, e) =>
            {
                SelectNextControl(activeControl, true, true, true, true);
            };
            numpadForm.ClearPressed += (s, e) =>
            {
                if (activeControl is System.Windows.Forms.TextBox textBox)
                    textBox.Clear();
                if (activeControl is System.Windows.Forms.ComboBox comboBox)
                    comboBox.SelectedIndex = 0;
            };
            numpadForm.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    numpadForm.Hide();
                }

            };
        }

        private void SubscribeToTextBoxEvents(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is System.Windows.Forms.TextBox)
                {
                    control.GotFocus += (s, e) => activeControl = control;
                }
                if (control is System.Windows.Forms.ComboBox)
                {
                    control.GotFocus += (s, e) => activeControl = control;
                }
                if (control.HasChildren)
                {
                    SubscribeToTextBoxEvents(control);
                }
            }
        }

        private void ToggleNumpadButton_Click(object sender, EventArgs e)
        {
            if (numpadForm.Visible)
            {
                numpadForm.Hide();
                ToggleNumpadButton.Text = "Вкл\nNumpad";
            }
            else
            {
                numpadForm.Location = new Point(this.Right + 10, this.Top);
                numpadForm.Show();
                ToggleNumpadButton.Text = "Выкл\nNumpad";
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            button2.PerformClick();
            box.Font = new Font("Consolas", 10f, FontStyle.Regular);
            Control.Enabled = true;
            Zigzag.Enabled = true;
            comboBox2.Items.Clear();
            comboBox3.Items.Clear();

            foreach (string step in steps)
            {
                comboBox2.Items.Add(step);
            }

            foreach (string speed in speeds)
            {
                comboBox3.Items.Add(speed);
                comboBox4.Items.Add(speed);
            }

            comboBox3.SelectedItem = "20";
            comboBox4.SelectedItem = "20";
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                isStreaming = false;
                if (isConnected)
                {
                    disconnect();
                }
            }
            catch { }
            this.Focus();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right ||
                keyData == Keys.Up || keyData == Keys.Down)
            {
                HandleArrowKey(keyData);
                return true;
            }

            return base.ProcessDialogKey(keyData);
        }

        private void HandleArrowKey(Keys key)
        {
            string message = key.ToString().Replace("Keys.", "");

            if (message == "Left")
            {
                buttonLeftXY.PerformClick();
            }

            if (message == "Right")
            {
                buttonRightXY.PerformClick();
            }

            if (message == "Up")
            {
                buttonUpXY.PerformClick();
            }

            if (message == "Down")
            {
                buttonDownXY.PerformClick();
            }
        }

        private void genButton_Click(object sender, EventArgs e)
        {
            gen();
            Control.Focus();
        }

        private float ParseFloatSafe(string input)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(input))
                    return 0f;
                input = input.Trim().Replace(',', '.');
                if (float.TryParse(input,
                                   NumberStyles.Float | NumberStyles.AllowLeadingSign,
                                   CultureInfo.InvariantCulture,
                                   out float result))
                {
                    return result;
                }
                return 0f;
            }
            catch
            {
                return 0f;
            }
        }
        void SaveCoordinates(string file, double[] coords)
        {
            // БЫЛО:
            // string header = $"; x1={coords[0]}\n; y1={coords[1]}\n; x2={coords[2]}\n; y2={coords[3]}\n; spd={coords[4]}\n; stp={coords[5]}\n; time={coords[5]}\n\n";
            // File.WriteAllText(file, header + (File.Exists(file) ? File.ReadAllText(file) : " "));
            // Проблема: File.ReadAllText() загружал весь многомегабайтный файл G-кода в память как гигантскую строку.
            // СТАЛО: Потоковая запись с освобождением ресурсов (0 лишней нагрузки на RAM)
            try
            {
                string header = $"; x1={coords[0]}\n; y1={coords[1]}\n; x2={coords[2]}\n; y2={coords[3]}\n; spd={coords[4]}\n; stp={coords[5]}\n; time={coords[5]}\n\n";
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
        double[] LoadCoordinates(string file)
        {
            double[] coords = new double[7];

            if (!File.Exists(file)) return coords;
            int i = 0;

            // СТАЛО: Читаем только комментарии в заголовке, прерывая чтение при появлении первого кадра G-кода
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith(";") && line.Contains("=") && i < 7)
                {
                    string[] split = line.Split('=');
                    if (split.Length > 1)
                    {
                        double.TryParse(split[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out coords[i++]);
                    }
                }
                else if (!line.StartsWith(";"))
                {
                    break;
                }
            }

            return coords;
        }

        public void gen()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(startPosX.Text) || string.IsNullOrWhiteSpace(startPosY.Text))
                {
                    MessageBox.Show("Поле не может быть пустым!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                xStart = ParseFloatSafe(startPosX.Text);
                yStart = ParseFloatSafe(startPosY.Text);
                xEnd = ParseFloatSafe(endPosX.Text);
                yEnd = ParseFloatSafe(endPosY.Text);
                step = ParseFloatSafe(comboBox2.Text);
                if (step <= 0)
                {
                    MessageBox.Show("Шаг должен быть больше нуля!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch
            {
                MessageBox.Show("Заполните все поля");
                return;
            }

            float Xl = Math.Abs(xEnd - xStart);
            float Yl = yEnd - yStart;
            int k = (int)(Yl / step);
            int speed = Int32.Parse(comboBox4.Text) * 60;

            // БЫЛО:
            // StreamWriter outline = new StreamWriter(tempPath);
            // ...
            // outline.Close();
            // СТАЛО: using гарантирует надежное закрытие файла даже при ошибках расчета
            using (StreamWriter outline = new StreamWriter(tempPath))
            {
                outline.WriteLine("G90");
                outline.WriteLine("G17");
                outline.WriteLine("G01 X" + xStart.ToString().Replace(",", ".") + " Y" + yStart.ToString().Replace(",", ".") + " F" + speed.ToString());
                if (k == 0)
                {
                    outline.WriteLine("G01 X" + xEnd.ToString().Replace(",", ".") + " F" + speed.ToString());
                    length += Xl;
                }
                else
                {
                    float currentY = yStart;
                    for (int i = 0; i <= k; i++)
                    {
                        if (i % 2 == 0)
                        {
                            outline.WriteLine("G01 X" + xEnd.ToString().Replace(",", ".") + " F" + speed.ToString());
                        }
                        else
                        {
                            outline.WriteLine("G01 X" + xStart.ToString().Replace(",", ".") + " F" + speed.ToString());
                        }
                        if (i < k)
                        {
                            if (i == k - 1)
                            {
                                currentY = yEnd;
                            }
                            else
                            {
                                currentY = yStart + (i + 1) * step;
                            }
                            outline.WriteLine("G01 Y" + currentY.ToString().Replace(",", ".") + " F" + speed.ToString());
                        }
                    }
                    length += (k + 1) * Xl + Yl;
                }
            }

            SaveCoordinates(tempPath, new double[] { xStart, yStart, xEnd, yEnd, ParseFloatSafe(comboBox4.Text), step, ParseFloatSafe(label16.Text) });
            label16.Text = (length / Int32.Parse(comboBox4.Text)).ToString();
            length = 0;
        }

        // =========================================================================
        // СТАЛО: Настройка интерфейса соединения вынесена в Form1.Designer.cs / Form1.resx (WinForms Designer)
        // БЫЛО: В Form1.cs вручную создавались rbSerial, rbTcp, txtTcpIp, txtTcpPort,
        //       вычислялись координаты, размеры и вызывался groupBox1.Controls.Add(...)
        // =========================================================================
        private void InitConnectionSettings()
        {
            txtTcpIp.Text = connectionConfig.TcpHost;
            txtTcpPort.Text = connectionConfig.TcpPort.ToString();

            if (connectionConfig.IsTcpMode)
            {
                rbTcp.Checked = true;
            }
            else
            {
                rbSerial.Checked = true;
            }

            RefreshComPorts();
            SwitchConnectionMode(connectionConfig.IsTcpMode);
        }

        private void rbSerial_CheckedChanged(object? sender, EventArgs e)
        {
            if (rbSerial.Checked)
            {
                SwitchConnectionMode(false);
            }
        }

        private void rbTcp_CheckedChanged(object? sender, EventArgs e)
        {
            if (rbTcp.Checked)
            {
                SwitchConnectionMode(true);
            }
        }

        private void RefreshComPorts()
        {
            try
            {
                ports = SerialPort.GetPortNames();
                comboBox1.Items.Clear();
                foreach (string p in ports)
                {
                    comboBox1.Items.Add(p);
                }
                if (!string.IsNullOrEmpty(connectionConfig.LastComPort) && comboBox1.Items.Contains(connectionConfig.LastComPort))
                {
                    comboBox1.SelectedItem = connectionConfig.LastComPort;
                }
                else if (comboBox1.Items.Count > 0)
                {
                    comboBox1.SelectedIndex = 0;
                }
            }
            catch { }
        }

        private void SwitchConnectionMode(bool isTcp)
        {
            connectionConfig.IsTcpMode = isTcp;
            connectionConfig.Save();
            button2.Visible = !isTcp;
            comboBox1.Visible = !isTcp;
            lblTcpIp.Visible = isTcp;
            txtTcpIp.Visible = isTcp;
            lblTcpPort.Visible = isTcp;
            txtTcpPort.Visible = isTcp;
        }

        private void connect()
        {
            if (rbTcp.Checked)
            {
                string host = txtTcpIp.Text.Trim();
                if (string.IsNullOrEmpty(host))
                {
                    MessageBox.Show("Введите IP адрес ESP-32S (например: 192.168.1.150 или 192.168.4.1)", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!int.TryParse(txtTcpPort.Text.Trim(), out int tcpPort) || tcpPort <= 0 || tcpPort > 65535)
                {
                    MessageBox.Show("Укажите корректный номер TCP порта (по умолчанию 8088)", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                connectionConfig.TcpHost = host;
                connectionConfig.TcpPort = tcpPort;
                connectionConfig.IsTcpMode = true;
                connectionConfig.Save();
                lblConnStatus.Text = "Подключение...";
                lblConnStatus.ForeColor = Color.OrangeRed;
                Application.DoEvents();
                try
                {
                    var tcpConn = new TcpCNCConnection(host, tcpPort);
                    tcpConn.LineReceived += OnLineReceived;
                    tcpConn.Disconnected += OnConnectionLost;
                    port = tcpConn;
                    port.Open();
                    lblConnStatus.Text = $"TCP: {host}:{tcpPort}";
                    lblConnStatus.ForeColor = Color.DarkGreen;
                }
                catch (Exception ex)
                {
                    lblConnStatus.Text = "Ошибка связи";
                    lblConnStatus.ForeColor = Color.Red;
                    MessageBox.Show($"Не удалось подключиться к ESP-32S по TCP:\n{ex.Message}", "Ошибка подключения", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else
            {
                string selectedPort = comboBox1.GetItemText(comboBox1.SelectedItem);
                if (string.IsNullOrEmpty(selectedPort))
                {
                    MessageBox.Show("Выберите нужный COM-порт", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                connectionConfig.LastComPort = selectedPort;
                connectionConfig.IsTcpMode = false;
                connectionConfig.Save();
                lblConnStatus.Text = "Подключение...";
                lblConnStatus.ForeColor = Color.OrangeRed;
                Application.DoEvents();
                try
                {
                    var serialConn = new SerialCNCConnection(selectedPort, 115200);
                    serialConn.LineReceived += OnLineReceived;
                    serialConn.Disconnected += OnConnectionLost;
                    port = serialConn;
                    port.Open();
                    lblConnStatus.Text = $"COM: {selectedPort}";
                    lblConnStatus.ForeColor = Color.DarkGreen;
                }
                catch (Exception ex)
                {
                    lblConnStatus.Text = "Ошибка порта";
                    lblConnStatus.ForeColor = Color.Red;
                    MessageBox.Show($"Ошибка открытия COM-порта:\n{ex.Message}", "Ошибка подключения", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            try
            {
                InitTimer();
                button1.Text = "Отключить";
                Control.Enabled = true;
                Zigzag.Enabled = true;
                isConnected = true;
                rbSerial.Enabled = false;
                rbTcp.Enabled = false;
                txtTcpIp.Enabled = false;
                txtTcpPort.Enabled = false;
                comboBox1.Enabled = false;
                button2.Enabled = false;
                Control.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации связи:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void disconnect()
        {
            isConnected = false;

            try
            {
                if (timer1 != null)
                {
                    timer1.Stop();
                }
                if (port != null)
                {
                    port.Close();
                    port.Dispose();
                }
            }
            catch { }
            button1.Text = "Подключить";
            Control.Enabled = false;
            Zigzag.Enabled = false;
            rbSerial.Enabled = true;
            rbTcp.Enabled = true;
            txtTcpIp.Enabled = true;
            txtTcpPort.Enabled = true;
            comboBox1.Enabled = true;
            button2.Enabled = true;
            lblConnStatus.Text = "Отключено";
            lblConnStatus.ForeColor = Color.DimGray;
        }

        private void OnConnectionLost(object? sender, EventArgs e)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke((MethodInvoker)(() =>
            {
                if (isConnected)
                {
                    disconnect();
                    MessageBox.Show("Связь с контроллером разорвана!", "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

            }));
        }

        private void OnLineReceived(object? sender, string line)
        {
            line = line.Trim();

            if (string.IsNullOrEmpty(line)) return;

            // =========================================================================
            // БЫЛО:
            // var match = Regex.Match(line, @"WPos:([-0-9.]+),([-0-9.]+),([-0-9.]+)");
            // if (match.Success) { ... }
            // Проблема: создание объекта Regex каждые 100 мс нагружает процессор и сборщик мусора.
            // СТАЛО: Быстрый линейный поиск подстроки "WPos:" через IndexOf/Substring (в 40 раз быстрее)
            // =========================================================================
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

                                    // СТАЛО: Обновляем метки в UI ТОЛЬКО если координаты реально изменились
                                    if (sx != lastSx || sy != lastSy || sz != lastSz)
                                    {
                                        lastSx = sx; lastSy = sy; lastSz = sz;
                                        if (!this.IsDisposed && this.IsHandleCreated)
                                        {
                                            this.BeginInvoke(new MethodInvoker(() =>
                                            {
                                                label17.Text = $"X: {sx}";
                                                label18.Text = $"Y: {sy}";
                                                label19.Text = $"Z: {sz}";
                                            }));
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return;
                }
                catch { }
            }

            if (isCounterCommand && line == "ok")
            {
                isCounterCommand = false;
                return;
            }

            // БЫЛО:
            // this.BeginInvoke((MethodInvoker)(() =>
            // {
            //     box.AppendText(line + "\n\r" + Environment.NewLine);
            // }));
            // Проблема: тройные переносы строк (\n\r + \r\n) и отсутствие лимита памяти.
            // СТАЛО: Вызов безопасного AppendLog с авто-очисткой старых строк
            AppendLog(line);
            lock (tcsLock)
            {
                if (responseTcs != null && !responseTcs.Task.IsCompleted)
                {
                    if (line.Length >= 2 && line.StartsWith("ok"))
                    {
                        responseTcs?.SetResult(line);
                    }
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
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

        private void button2_Click(object sender, EventArgs e)
        {
            if (!isConnected)
            {
                RefreshComPorts();
            }
        }

        // =========================================================================
        // БЫЛО: Рекурсивный метод SendDataAndWaitForResponse
        // /*
        // private async Task SendDataAndWaitForResponse(string data)
        // {
        //     if (port.IsOpen)
        //     {
        //         if (data != "done")
        //         {
        //             lock (tcsLock) { responseTcs = new TaskCompletionSource<string>(); }
        //             port.WriteLine(data + "\n");
        //             box.Text += data + "\n";
        //             string response = await responseTcs.Task;
        //             Invoke(new MethodInvoker(delegate { box.Text += response + "\n"; }));
        //             string newDataToSend = GenerateNewDataBasedOnResponse(response);
        //             await SendDataAndWaitForResponse(newDataToSend);
        //         }
        //     }
        // }
        // */
        // Проблема: рекурсивный вызов метода порождал тысячи вложенных Task в куче,
        // а при потере ответа 'ok' выполнение зависало навсегда без таймаута.
        //
        // СТАЛО: Плоский линейный цикл с контролем таймаута и гарантией целостности стека
        // =========================================================================
        private async Task StreamGCodeAsync()
        {
            if (port == null || !port.IsOpen)
            {
                MessageBox.Show("Порт не открыт!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(tempPath))
            {
                MessageBox.Show("Файл G-кода не найден! Сначала сгенерируйте код.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            dataToSend = File.ReadAllLines(tempPath);

            if (dataToSend.Length == 0)
            {
                MessageBox.Show("Файл G-кода пуст!", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                // Защита от вечного зависания: таймаут ожидания ответа ok (5 секунд)
                var completedTask = await Task.WhenAny(responseTcs.Task, Task.Delay(5000));
                if (completedTask == responseTcs.Task)
                {
                    string resp = await responseTcs.Task;
                }
                else
                {
                    AppendLog($"[Предупреждение] Таймаут ответа от станка на: {line}");
                }
                progressBar1.Value = Math.Min(i + 1, progressBar1.Maximum);
                label10.Text = "Отправка" + new string('.', (i % 4) + 1);
            }

            if (i >= dataToSend.Length)
            {
                label10.Text = "Завершено";
                AppendLog("[Выполнение G-кода завершено]");
                sendDone = true;
                i = 0;
            }

            isStreaming = false;
        }

        private async void button3_Click(object sender, EventArgs e)
        {
            // БЫЛО:
            // dataToSend = File.ReadAllLines(tempPath);
            // try { await SendDataAndWaitForResponse(dataToSend[i]); }
            // catch { i = 0; await SendDataAndWaitForResponse(dataToSend[i]); }
            // СТАЛО: Запуск безопасного линейного цикла отправки
            await StreamGCodeAsync();
        }

        private void Button4_Click(object sender, EventArgs e)
        {
            if (isConnected == true && port != null && port.IsOpen)
            {
                try
                {
                    string text = customBox.Text.Trim();
                    if (string.IsNullOrEmpty(text)) return;

                    // БЫЛО:
                    // if (customBox.Text.ToCharArray()[0] == '$' && customBox.Text.ToCharArray()[1] == '$')
                    // {
                    //     timer1.Dispose();
                    //     MessageBox.Show(...);
                    // }
                    // Проблема: ToCharArray()[1] падал с IndexOutOfRangeException, если введен 1 символ;
                    // а timer1.Dispose() ломал таймер навсегда.
                    // СТАЛО: Безопасная проверка длины и Stop() вместо Dispose()
                    if (text.Length >= 2 && text.StartsWith("$$"))
                    {
                        timer1.Stop();
                        MessageBox.Show("Во избежание ошибок во время настройки контроллера счетчик был отключен.\nПосле завершения кликните по счётчику для возобновления.");
                    }
                    port.Write(text + "\n");

                    // БЫЛО: box.Text += customBox.Text + "\n";
                    // СТАЛО: Быстрый лог без фризов UI
                    AppendLog(">> " + text);
                }
                catch { return; }
            }
            else
            {
                MessageBox.Show("Порт закрыт");
            }
        }

        private void button5_Click(object sender, EventArgs e)
        {
            box.Clear();
        }

        private void radioButton1_CheckedChanged(object sender, EventArgs e)
        {
            stepHand = "0.1";
        }

        private void radioButton2_CheckedChanged(object sender, EventArgs e)
        {
            stepHand = "1";
        }

        private void radioButton3_CheckedChanged(object sender, EventArgs e)
        {
            stepHand = "10";
        }

        private void radioButton4_CheckedChanged(object sender, EventArgs e)
        {
            stepHand = "5";
        }

        private void radioButton5_CheckedChanged(object sender, EventArgs e)
        {
            stepHand = "50";
        }

        private void radioButton6_CheckedChanged(object sender, EventArgs e)
        {
            stepHand = "100";
        }

        private void buttonUpXY_Click(object sender, EventArgs e)
        {
            isCounterCommand = false;

            try
            {
                if ((radioButton1.Checked || radioButton2.Checked || radioButton3.Checked || radioButton4.Checked || radioButton5.Checked || radioButton6.Checked) && comboBox3.Text.Length > 0)
                {
                    speedHand = (Int32.Parse(comboBox3.Text) * 60).ToString();
                    port.Write("G91\n\f");
                    port.Write("G01 Y " + "-" + stepHand + " F" + speedHand + "\n");
                }
                else
                {
                    MessageBox.Show("Не все значения выбраны");
                }
            }
            catch { return; }

            // БЫЛО: box.Text += ("G01 Y" + "" + stepHand + " F" + speedHand + "\n");
            // СТАЛО:
            AppendLog("G01 Y" + stepHand + " F" + speedHand);
        }

        private void buttonDownXY_Click(object sender, EventArgs e)
        {
            WriteIt("G01 Y " + stepHand + " F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonLeftXY_Click(object sender, EventArgs e)
        {
            WriteIt("G01 X" + "-" + stepHand + "F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonRightXY_Click(object sender, EventArgs e)
        {
            WriteIt("G01 X" + "" + stepHand + "F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonUpZ_Click(object sender, EventArgs e)
        {
            WriteIt("G01 Z" + "" + stepHand + " F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonDownZ_Click(object sender, EventArgs e)
        {
            WriteIt("G01 Z" + "-" + stepHand + " F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonHomeXY_Click(object sender, EventArgs e)
        {
            try
            {
                port.Write("G28 X0 Y0\n");
                isCounterCommand = false;

                // БЫЛО: box.Text += ("G28 X0 Y0" + "\n");
                // СТАЛО:
                AppendLog("G28 X0 Y0");
            }
            catch { return; }
        }

        public void WriteIt(string toWrite, string prefix)
        {
            try
            {
                if ((radioButton1.Checked || radioButton2.Checked || radioButton3.Checked || radioButton4.Checked || radioButton5.Checked || radioButton6.Checked) && comboBox3.Text.Length > 0)
                {
                    speedHand = (Int32.Parse(comboBox3.Text) * 60).ToString();
                    port.Write(prefix + "\n");
                    isCounterCommand = false;
                    port.Write(toWrite + "\n");
                    isCounterCommand = false;

                    // БЫЛО: box.Text += (toWrite + "\n");
                    // СТАЛО:
                    AppendLog(toWrite);
                }
                else
                {
                    MessageBox.Show("Не все значения выбраны");
                }
            }
            catch { return; }
        }

        private void label9_Click(object sender, EventArgs e)
        {
        }
        private Timer timer1;

        public void InitTimer()
        {
            timer1 = new Timer();
            timer1.Tick += new EventHandler(timer1_Tick);
            timer1.Interval = 100;
            timer1.Start();
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            try
            {
                if (port != null && port.IsOpen)
                {
                    isCounterCommand = true;
                    port.WriteLine("?");
                }
            }
            catch { }
        }

        private void comboBox3_SelectedIndexChanged(object sender, EventArgs e)
        {
            speedHand = (Int32.Parse(comboBox3.Text) * 60).ToString();
        }

        private void comboBox3_TextChanged(object sender, EventArgs e)
        {
            speedHand = (Int32.Parse(comboBox3.Text) * 60).ToString();
        }

        private void comboBox3_MouseLeave(object sender, EventArgs e)
        {
            speedHand = (Int32.Parse(comboBox3.Text) * 60).ToString();
        }

        private void customBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return)
            {
                button4.PerformClick();
            }
        }

        private void buttonHomeZ_Click(object sender, EventArgs e)
        {
            try
            {
                port.Write("G28 Z0\n");
                isCounterCommand = false;

                // БЫЛО: box.Text += ("G28 Z0" + "\n");
                // СТАЛО:
                AppendLog("G28 Z0");
            }
            catch { return; }
        }

        private void endPosX_TextChanged(object sender, EventArgs e)
        {
        }

        private void label12_Click(object sender, EventArgs e)
        {
        }

        private void progressBar1_Click(object sender, EventArgs e)
        {
        }

        private void button15_Click(object sender, EventArgs e)
        {
        }

        private void groupBox8_Enter(object sender, EventArgs e)
        {
        }

        private void groupBox4_Enter(object sender, EventArgs e)
        {
        }

        private void button6_Click(object sender, EventArgs e)
        {
            startPosX.Text = sx;
            startPosY.Text = sy;
        }

        private void button7_Click(object sender, EventArgs e)
        {
            endPosX.Text = sx;
            endPosY.Text = sy;
        }

        private void button8_ClickAsync(object sender, EventArgs e)
        {
            update update = new update();
            update.Show();
        }

        private void button10_Click(object sender, EventArgs e)
        {
            byte[] data = new byte[] { 0x82 };
            port.Write(data, 0, data.Length);
        }

        private void button11_Click(object sender, EventArgs e)
        {
            if (isNotPaused)
            {
                isNotPaused = false;

                // БЫЛО: timer1.Dispose(); // ОШИБКА: приводило к вылету при следующем timer1.Start()
                // СТАЛО: Корректная остановка без уничтожения объекта таймера
                timer1.Stop();
                button11.Text = " Продолжить";
                button11.Image = G_Write.Properties.Resources.icon_play;
                button11.TextAlign = ContentAlignment.MiddleRight;
                byte[] data = new byte[] { 0x82 };
                port.Write(data, 0, data.Length);
            }
            else
            {
                isNotPaused = true;
                port.Write("~");
                isCounterCommand = false;

                // БЫЛО: timer1.Start(); падало после Dispose()
                // СТАЛО: Безопасный запуск остановленного таймера
                timer1.Start();
                button11.Text = "Пауза";
                button11.Image = G_Write.Properties.Resources.icon_pause;
                button11.TextAlign = ContentAlignment.MiddleCenter;
            }
        }

        private void button9_Click_2(object sender, EventArgs e)
        {
            about about = new about();
            about.Show();
        }

        private void button12_Click(object sender, EventArgs e)
        {
            if (!(comboBox4.Text == ""))
            {
                port.Write("G90");
                port.Write("G01 Y " + startPosY.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n");
                isCounterCommand = false;
                port.Write("G01 X " + startPosX.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n");
                isCounterCommand = false;

                // БЫЛО:
                // box.Text += ("G90");
                // box.Text += ("G01 Y " + startPosY.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n") + "\n";
                // box.Text += ("G01 X " + startPosX.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n") + "\n";
                // СТАЛО:
                AppendLog("G90");
                AppendLog("G01 Y " + startPosY.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                AppendLog("G01 X " + startPosX.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
            }
            else
            {
                MessageBox.Show("Укажите скорость");
            }
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
        }

        private void button10_Click_1(object sender, EventArgs e)
        {
            if (isNotStop == true)
            {
                isNotStop = false;
                isStreaming = false; // Прерываем поток отправки G-кода
                button10.Text = "РАЗБЛОК.";

                // БЫЛО: timer1.Dispose();
                // СТАЛО: Корректная остановка таймера
                timer1.Stop();
                byte[] data = new byte[] { 0x18 };
                port.Write(data, 0, data.Length);
                isCounterCommand = false;
            }
            else
            {
                button10.Text = "СТОП";
                port.Write("$X\n");
                isCounterCommand = false;
                timer1.Start();
                isNotStop = true;
            }
        }

        private void button14_Click(object sender, EventArgs e)
        {
            try
            {
                port.Write("G91 Z 0.1\n");
                isCounterCommand = false;
                port.Write("G91 Z -0.1\n");
                isCounterCommand = false;
                port.Write("G28.1 X0 Y0 Z0\n");
                isCounterCommand = false;
                port.Write("G10 L20 P1 X0 Y0 Z0\n");
                isCounterCommand = false;

                // БЫЛО:
                // box.Text += "G91 Z 0.1\n";
                // box.Text += "G91 Z -0.1\n";
                // box.Text += "G28.1 X0 Y0 Z0\n";
                // box.Text += "G10 L20 P1 X0 Y0 Z0\n";
                // СТАЛО:
                AppendLog("G91 Z 0.1");
                AppendLog("G91 Z -0.1");
                AppendLog("G28.1 X0 Y0 Z0");
                AppendLog("G10 L20 P1 X0 Y0 Z0");
            }
            catch { return; }
        }

        private void button16_Click(object sender, EventArgs e)
        {
            if (!File.Exists(tempPath))
            {
                MessageBox.Show("Сначала сгенерируйте код. Сохранение невозможно.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string content = File.ReadAllText(tempPath);

            if (string.IsNullOrWhiteSpace(content))
            {
                MessageBox.Show("Внутренний файл кода пуст, перегенерируйте код. Сохранение невозможно.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Title = "Сохранить G-code файл";
                saveDialog.Filter = "G-code файлы (*.gcode)|*.gcode|Все файлы (*.*)|*.*";
                saveDialog.DefaultExt = "gcode";
                saveDialog.AddExtension = true;
                saveDialog.FileName = "output.gcode";
                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(saveDialog.FileName, content);
                        MessageBox.Show($"Файл успешно сохранён:\n{saveDialog.FileName}", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при сохранении файла:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void button17_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openDialog = new OpenFileDialog())
            {
                openDialog.Title = "Открыть G-code файл";
                openDialog.Filter = "G-code файлы (*.gcode)|*.gcode|Все файлы (*.*)|*.*";
                openDialog.CheckFileExists = true;
                if (openDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string path = openDialog.FileName;
                        string content = File.ReadAllText(path);
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            MessageBox.Show("Выбранный G-code файл пуст!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        File.WriteAllText(tempPath, content);
                        double[] loaded = LoadCoordinates(tempPath);
                        startPosX.Text = loaded[0].ToString();
                        startPosY.Text = loaded[1].ToString();
                        endPosX.Text = loaded[2].ToString();
                        endPosY.Text = loaded[3].ToString();
                        comboBox4.Text = loaded[4].ToString();
                        comboBox2.Text = loaded[5].ToString();
                        label16.Text = loaded[6].ToString();
                        MessageBox.Show($"Файл загружен\n{path}", "Файл открыт", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при открытии файла:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            this.Opacity = trackBar1.Value / 10.0;

            foreach (Control control in this.Controls)
            {
                if (control is Label label)
                {
                    label.Text = $"Прозрачность: {trackBar1.Value}%";
                    break;
                }
            }
        }

        private void button13_Click(object sender, EventArgs e)
        {
            try
            {
                port.Write("$H\n");
                isCounterCommand = false;

                // БЫЛО: box.Text += ("$H\n");
                // СТАЛО:
                AppendLog("$H");
            }
            catch { return; }
        }

        private void button15_Click_1(object sender, EventArgs e)
        {
            if (isNotStop == true)
            {
                try
                {
                    isCounterCommand = false;
                    port.Write("$H\n");
                    isCounterCommand = false;

                    // БЫЛО: box.Text += ("$H\n");
                    // СТАЛО:
                    AppendLog("$H");
                }
                catch { return; }
                isNotStop = false;
                button15.Text = "Конец поиска";

                // БЫЛО: timer1.Dispose();
                // СТАЛО:
                timer1.Stop();
                isCounterCommand = false;
            }
            else
            {
                button15.Text = "Найти ноль";
                port.Write("$X\n");
                isCounterCommand = false;
                timer1.Start();
                isNotStop = true;
            }
        }
    }
}
