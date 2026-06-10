using System.Data;
using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.IO.Ports;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Timer = System.Windows.Forms.Timer;
namespace G_write
{
    public partial class Form1 : Form
    {
        private bool isCounterCommand = false;
        double x, y, z;
        string sx, sy, sz;
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
        String[] steps = ["10", "20", "50", "60"];
        String[] speeds = ["4", "10", "20", "40"];
        String stepHand = "10";
        String speedHand = (Int32.Parse("10") * 60).ToString();
        SerialPort port;
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
        public bool ok = true;
        string[] dataToSend;
        //private bool waitingForResponse = false;
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
            //SubscribeToComboBoxEvents(this);
            button11.ImageAlign = ContentAlignment.MiddleLeft;
            //version();

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
                    textBox.Text = textBox.Text[..^1]; // Удаляем последний символ
                if (activeControl is System.Windows.Forms.ComboBox comboBox && comboBox.Text.Length > 0)
                    comboBox.Text = comboBox.Text[..^1]; // Удаляем последний символ
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
        /*
        private void SubscribeToComboBoxEvents(Control parent)
        {
            foreach (Control control1 in parent.Controls)
            {
                if (control1 is System.Windows.Forms.ComboBox)
                {
                    control1.GotFocus += (s, e) => activeControl = control1;
                }

                if (control1.HasChildren)
                {
                    SubscribeToTextBoxEvents(control1);
                }
            }
        }
        */
        // Обработчик для кнопки показа/скрытия numpad
        private void ToggleNumpadButton_Click(object sender, EventArgs e)
        {
            if (numpadForm.Visible)
            {
                numpadForm.Hide();
                ToggleNumpadButton.Text = "Экранная\nклавиатура";
            }
            else
            {
                numpadForm.Location = new Point(this.Right + 10, this.Top);
                numpadForm.Show();
                ToggleNumpadButton.Text = "Закрыть\nклавиатуру";
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {


            button2.PerformClick();
            box.Font = new Font("Consolas", 10f, FontStyle.Regular);
            Control.Enabled = false;
            Zigzag.Enabled = false;
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
            comboBox3.SelectedItem = "10";

        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Перенаправляем фокус на форму или другой контрол в UI-потоке
            this.Focus(); // или на panel1, если это предпочтительнее
        }
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right ||
                keyData == Keys.Up || keyData == Keys.Down)
            {
                // Вызов метода для обработки стрелок
                HandleArrowKey(keyData);
                return true; // Клавиша обработана
            }
            return base.ProcessDialogKey(keyData);
        }

        private void HandleArrowKey(Keys key)
        {
            string message = key.ToString().Replace("Keys.", ""); // Пример: "Left"
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
            //MessageBox.Show(message, "Нажатие стрелки");
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

                // Разрешаем знаки +/− и десятичную точку
                if (float.TryParse(input,
                                   NumberStyles.Float | NumberStyles.AllowLeadingSign,
                                   CultureInfo.InvariantCulture,
                                   out float result))
                {
                    return result;
                }

                return 0f; // если парсинг не удался
            }
            catch
            {
                return 0f; // если вообще что-то пошло не так
            }
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
                /*
                xStart = float.Parse(startPosX.Text);
                yStart = float.Parse(startPosY.Text);
                xEnd = float.Parse(endPosX.Text);
                yEnd = float.Parse(endPosY.Text);
                step = float.Parse(comboBox2.Text);
                MessageBox.Show(xStart + "\n" + yStart + "\n" + xEnd + "\n" + yEnd + "\n");
                //step = float.Parse(TxtStep.Text);
                */

            }
            catch
            {
                MessageBox.Show("Заполните все поля"); return;
            }
            //StreamWriter outline = new StreamWriter(mydocu + @"\code.gcode");
            StreamWriter outline = new StreamWriter(tempPath);
            float Xl = xEnd - xStart;
            float Yl = yEnd - yStart;
            int nY = (int)Math.Truncate(Yl / step);
            float tY = (Yl / step - nY) * step;
            int pY = 1;
            outline.WriteLine("G90");
            outline.WriteLine("G17");
            outline.WriteLine("G01" + " X " + xStart.ToString().Replace(",", ".") + " Y " + yStart.ToString().Replace(",", ".") + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
            outline.WriteLine("G01" + " X " + xEnd.ToString().Replace(",", ".") + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
            length += (xEnd - xStart);
            if (yEnd <= step)
            {

            }
            else
            {
                while (!(pY > nY))
                {
                    if (!((pY % 2) == 0))
                    {
                        outline.WriteLine("G01 " + " Y " + pY * step + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        outline.WriteLine("G01 " + " X " + xStart.ToString().Replace(",", ".") + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        length += (step + (xEnd - xStart));
                        pY++;

                    }
                    else
                    {
                        outline.WriteLine("G01 " + " Y " + pY * step + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        outline.WriteLine("G01 " + " X " + xEnd.ToString().Replace(",", ".") + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        length += (step + (xEnd - xStart));
                        pY++;
                    }
                }
                if (((yEnd % step) != 0))
                {
                    if (!((pY % 2) == 0))
                    {
                        outline.WriteLine("G01 " + " Y " + (tY + (pY - 1) * step) + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        outline.WriteLine("G01 " + " X " + xStart.ToString().Replace(",", ".") + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        length += (tY + (xEnd - xStart));
                    }
                    if ((pY % 2) == 0)
                    {
                        outline.WriteLine("G01 " + " Y " + (tY + (pY - 1) * step) + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        outline.WriteLine("G01 " + " X " + xEnd.ToString().Replace(",", ".") + " " + "F" + (Int32.Parse(comboBox4.Text) * 60).ToString());
                        length += (tY + (xEnd - xStart));
                    }
                }
            }
            outline.Close();

            //MessageBox.Show(length.ToString());
            label16.Text = (length / Int32.Parse(comboBox4.Text)).ToString();
            length = 0;

        }
        private void connect()
        {
            string selectedPort = comboBox1.GetItemText(comboBox1.SelectedItem);
            try
            {
                port = new SerialPort(selectedPort, 115200, Parity.None, 8, StopBits.One);
                //port.DataReceived += port_DataReceived;
                port.DataReceived += new SerialDataReceivedEventHandler(DataReceivedHandler);
            }
            catch
            {
                MessageBox.Show("Выберите нужный порт"); return;
            }
            try
            {
                port.Open();
                InitTimer();
                button1.Text = "Оключить";
                Control.Enabled = true;
                Zigzag.Enabled = true;
                isConnected = true;
                Control.Focus();
            }
            catch
            {
                MessageBox.Show("Ошибка подключения"); return;
            }
            //port.Write("#STAR\n");
            //InitTimer();
        }

        private void disconnect()
        {
            isConnected = false;
            //timer1.Stop();
            //timer1.Dispose();
            //port.Write("#STOP\n");
            try
            {
                port.Close();
                timer1.Stop();
            }
            catch
            {
                return;
            }
            button1.Text = "Подключить";
            Control.Enabled = false;
            Zigzag.Enabled = false;
        }
        private void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            string line;// = string.Empty;

            try
            {
                line = port.ReadLine();
            }
            catch
            {
                return;
            }
            line = line.Trim();
            if (line.StartsWith("<") && line.Contains("WPos:"))
            {
                try
                {
                    //line = port.ReadLine();
                    var match = Regex.Match(line, @"WPos:([-0-9.]+),([-0-9.]+),([-0-9.]+)");
                    if (match.Success)
                    {
                        double x = Math.Round(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
                        double y = Math.Round(double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
                        double z = Math.Round(double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), 1);


                        sx = x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                        sy = y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                        sz = z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

                        this.BeginInvoke(new MethodInvoker(() =>
                        {
                            label17.Text = $"X: {sx}";
                            label18.Text = $"Y: {sy}";
                            label19.Text = $"Z: {sz}";
                        }));
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
            this.BeginInvoke((MethodInvoker)(() =>
            {
                box.AppendText(line + "\n\r" + Environment.NewLine);
            }));
            //string dataReceived = port.ReadLine();
            //receiveData = dataReceived;

            lock (tcsLock)
            {
                if (responseTcs != null && !responseTcs.Task.IsCompleted)
                {
                    if (line[0].ToString() == "o" && line[1].ToString() == "k")
                    {
                        responseTcs?.SetResult(line);
                    }
                }
            }
            // Invoke(new MethodInvoker(delegate
            // {
            //    try
            //     {
            //         if (isCounterCommand && dataReceived.Trim() == "ok")
            //        {
            //           return;
            //      }
            //      else if (dataReceived[1].ToString() != "" && dataReceived[1].ToString() != "" && dataReceived[1].ToString() != "A" && dataReceived[1].ToString() != "H")
            //      {
            //         box.Text += dataReceived + "\n";
            //     }
            //box.Text += dataReceived + "\n";
            // }
            // catch { return; }

            //}));

            //}
            //catch
            //{
            //MessageBox.Show("Непредвиденное отключение");
            //timer1.Stop();
            //return;
            //}

        }
        private void button1_Click(object sender, EventArgs e)
        {
            //Control.Focus();
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
            //Control.Focus();
            if (!isConnected)
            {
                ports = SerialPort.GetPortNames();
                comboBox1.Items.Clear();
                foreach (string port in ports)
                {
                    comboBox1.Items.Add(port);
                    Console.WriteLine(port);
                    if (ports[0] != null)
                    {
                        comboBox1.SelectedItem = ports[0];
                    }
                }
            }
        }

        private async Task SendDataAndWaitForResponse(string data)
        {
            if (port.IsOpen)
            {
                //if (isNotPaused)
                //{
                if (data != "done")
                {
                    lock (tcsLock) { responseTcs = new TaskCompletionSource<string>(); }
                    port.WriteLine(data + "\n");
                    isCounterCommand = false;
                    box.Text += data + "\n";
                    string response = await responseTcs.Task;
                    Invoke(new MethodInvoker(delegate
                    {
                        box.Text += response + "\n";
                    })); // Отправка новых данных после получения ответа

                    string newDataToSend = GenerateNewDataBasedOnResponse(response);
                    await SendDataAndWaitForResponse(newDataToSend);
                }
                else
                {
                    i = 0;
                    return;
                }
                //}
            }
        }

        private async void button3_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            //Sender();
            //dataToSend = File.ReadAllLines(mydocu + @"\code.gcode"); // Данные для отправки;
            dataToSend = File.ReadAllLines(tempPath);
            try
            {
                await SendDataAndWaitForResponse(dataToSend[i]);
            }
            catch
            {
                i = 0;
                await SendDataAndWaitForResponse(dataToSend[i]);

            }
        }

        private string GenerateNewDataBasedOnResponse(string response)
        { // Логика для генерации новых данных на основе ответа 
            i++;
            progressBar1.Minimum = 0; progressBar1.Maximum = dataToSend.Length;

            if (i < dataToSend.Length)
            {
                progressBar1.Value = i;
                label10.Text = "Отправка" + String.Concat(Enumerable.Repeat(".", j));
                j++;
                if (j >= 4)
                {
                    j = 0;
                }
                return dataToSend[i];
            }
            else
            {
                progressBar1.Value = i;
                label10.Text = "Не отправляется";
                //MessageBox.Show("Всё прислано");
                sendDone = true;
                return "done";
            }
        }
        private async void Button4_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            if (isConnected == true)
            {
                try
                {
                    if (customBox.Text.ToCharArray()[0] == '$' && customBox.Text.ToCharArray()[1] == '$')
                    {
                        timer1.Dispose();
                        MessageBox.Show("Во избежание ошибок во время настройки контроллера счетчик был отключен.\nПосле завершения кликните по счётчику для возобновления.");
                    }

                    //timer1.Stop();

                    port.Write(customBox.Text + "\n");
                }
                catch { return; }

                box.Text += customBox.Text + "\n";
            }
            else
            {
                MessageBox.Show("Порт закрыт");
            }
        }

        private void button5_Click(object sender, EventArgs e)
        {
            //Control.Focus();
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
            //Control.Focus();
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

            box.Text += ("G01 Y" + "" + stepHand + " F" + speedHand + "\n");
        }

        private void buttonDownXY_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            WriteIt("G01 Y " + stepHand + " F" + speedHand, "G91");
            isCounterCommand = false;
        }
        private void buttonLeftXY_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            WriteIt("G01 X" + "-" + stepHand + "F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonRightXY_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            WriteIt("G01 X" + "" + stepHand + "F" + speedHand, "G91");
            isCounterCommand = false;
        }

        /*
        public async void CounterNum()
        {
            int i = 10;
            try
            {
                if (port.IsOpen)
                {
                    port.Write("?");
                }
                else { return; }
            }
            catch
            {
                MessageBox.Show("Не удалось передать, возможно устройство отключено"); return;
            }
            await Task.Delay(15);
            try
            {
                if (receiveData != null && receiveData[1].ToString() == "R")
                {
                    while (receiveData[i].ToString() != "," && i <= 25)
                    {
                        currentX += receiveData[i].ToString();
                        i += 1;
                    }
                    i++;
                    while (receiveData[i].ToString() != "," && i <= 30)
                    {
                        currentY += receiveData[i].ToString();
                        i += 1;
                    }
                    i++;
                    while (receiveData[i].ToString() != "|" && i <= 45)
                    {
                        currentZ += receiveData[i].ToString();
                        i += 1;
                    }
                    label9.Invoke((MethodInvoker)(() => { label9.Text = "X: " + currentX.Remove(currentX.Length - 1) + "\nY: " + currentY.Remove(currentY.Length - 1) + "\nZ: " + currentZ.Remove(currentZ.Length - 1); }));
                    currentX_str = currentX.Remove(currentX.Length - 1);
                    currentY_str = currentY.Remove(currentY.Length - 1);
                    //label9.Text = ("X: " + currentX + " " + "Y: " + currentY + "\nZ: " + currentZ);
                }
                else if (receiveData != null && receiveData[1].ToString() == "I")
                {
                    i++;
                    while (receiveData[i].ToString() != "," && i <= 25)
                    {
                        currentX += receiveData[i].ToString();
                        i += 1;
                    }
                    i++;
                    while (receiveData[i].ToString() != "," && i <= 35)
                    {
                        currentY += receiveData[i].ToString();
                        i += 1;
                    }
                    i++;
                    while (receiveData[i].ToString() != "|" && i <= 45)
                    {
                        currentZ += receiveData[i].ToString();
                        i += 1;
                    }
                    //if(currentX.Length > 0 && currentY.Length > 0 && currentZ.Length > 0)
                    //{ 
                    label9.Invoke((MethodInvoker)(() => { try { label9.Text = "X: " + currentX.Remove(currentX.Length - 1) + "\nY: " + currentY.Remove(currentY.Length - 1) + "\nZ: " + currentZ.Remove(currentZ.Length - 1); } catch { return; } }));
                    currentX_str = currentX.Remove(currentX.Length - 1);
                    currentY_str = currentY.Remove(currentY.Length - 1);
                    //label9.Text = ("X: " + currentX + " " + "Y: " + currentY + "\nZ: " + currentZ);
                    //}
                }
                else
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            currentX = "";
            currentY = "";
            currentZ = "";
        }
        */
        private void buttonUpZ_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            WriteIt("G01 Z" + "" + stepHand + " F" + speedHand, "G91");
            isCounterCommand = false;
        }

        private void buttonDownZ_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            WriteIt("G01 Z" + "-" + stepHand + " F" + speedHand, "G91");
            isCounterCommand = false;
        }
        private void buttonHomeXY_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            try
            {
                //isCounterCommand = false;
                port.Write("G28 X0 Y0\n");
                isCounterCommand = false;
                box.Text += ("G28 X0 Y0" + "\n");
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
                    box.Text += (toWrite + "\n");
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
            //timer1.Start();
        }
        private Timer timer1;
        public void InitTimer()
        {
            timer1 = new Timer();
            timer1.Tick += new EventHandler(timer1_Tick);
            timer1.Interval = 100; // in miliseconds
            //Task task = Task.Run(() => CounterNum());
            timer1.Start();
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            //Task task = Task.Run(() => CounterNum());
            try
            {
                if (port.IsOpen)
                    isCounterCommand = true;
                port.WriteLine("?");
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
            //Control.Focus();
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
            //Control.Focus();
            try
            {
                port.Write("G28 Z0\n");
                isCounterCommand = false;
                box.Text += ("G28 Z0" + "\n");
            }

            catch { return; }
        }
        private void button6_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            startPosX.Text = sx;
            startPosY.Text = sy;
        }

        private void button7_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            endPosX.Text = sx;
            endPosY.Text = sy;
        }
        private void button8_ClickAsync(object sender, EventArgs e)
        {
            //Control.Focus();
            update update = new update();
            update.Show();
        }
        private void button10_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            byte[] data = new byte[] { 0x82 };
            port.Write(data, 0, data.Length);
        }

        private void button11_Click(object sender, EventArgs e)
        {
            //Control.Focus();

            if (isNotPaused)
            {
                isNotPaused = false;
                timer1.Dispose();
                button11.Text = " Продолжить";
                button11.Image = G_Write.Properties.Resources.icon_play;
                button11.TextAlign = ContentAlignment.MiddleRight;
                byte[] data = new byte[] { 0x82 };
                port.Write(data, 0, data.Length);
            }
            else
            {
                isNotPaused = true;
                if (!sendDone)
                {
                    //button3.PerformClick();
                    port.Write("~");
                    isCounterCommand = false;
                    sendDone = false;
                }
                else
                {
                    port.Write("~");
                    isCounterCommand = false;
                }
                timer1.Start();
                button11.Text = "Пауза";
                button11.Image = G_Write.Properties.Resources.icon_pause;
                button11.TextAlign = ContentAlignment.MiddleCenter;
            }
        }

        private void button9_Click_2(object sender, EventArgs e)
        {
            //Control.Focus();
            about about = new about();
            about.Show();
        }

        private void button12_Click(object sender, EventArgs e)
        {
            //Control.Focus();
            if (!(comboBox4.Text == ""))
            {
                port.Write("G90");
                box.Text += ("G90");
                port.Write("G01 Y " + startPosY.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n");
                isCounterCommand = false;
                box.Text += ("G01 Y " + startPosY.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n") + "\n";
                port.Write("G01 X " + startPosX.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n");
                box.Text += ("G01 X " + startPosX.Text.ToString().Replace(",", ".") + " F" + (Int32.Parse(comboBox4.Text) * 60).ToString() + "\n") + "\n";
                isCounterCommand = false;
            }
            else
            {
                MessageBox.Show("Укажите скорость");
            }
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            // Проверяем, какая клавиша была нажата
            /*
            switch (e.KeyCode)
            {
                case Keys.Up:
                    button4.PerformClick();
                    e.Handled = true; // Поглощаем событие
                    break;
                case Keys.Enter:
                    MessageBox.Show("Нажата клавиша Enter");
                    break;
                case Keys.Escape:
                    MessageBox.Show("Нажата клавиша Escape");
                    break;
                    // Добавьте другие клавиши по необходимости
            }
            */
        }

        private void button10_Click_1(object sender, EventArgs e)
        {
            //Control.Focus();
            if (isNotStop == true)
            {
                isNotStop = false;
                button10.Text = "РАЗБЛОК.";
                timer1.Dispose();
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
            //Control.Focus();
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
                box.Text += "G91 Z 0.1\n";
                box.Text += "G91 Z -0.1\n";
                box.Text += "G28.1 X0 Y0 Z0\n";
                box.Text += "G10 L20 P1 X0 Y0 Z0\n";
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

            // Читаем содержимое temp.txt
            string content = File.ReadAllText(tempPath);

            // Проверка, что файл не пустой (с учётом пробелов и переносов)
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

                        // Проверка, что файл не пустой
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            MessageBox.Show("Выбранный G-code файл пуст!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        // Записываем содержимое в temp.txt (в папке программы)                       
                        File.WriteAllText(tempPath, content);

                        MessageBox.Show(
                            $"Файл загружен\n{path}",
                            "Файл открыт", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            // Обновляем метку
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
                //isCounterCommand = false;
                port.Write("$H\n");
                isCounterCommand = false;
                box.Text += ("$H\n");
            }

            catch { return; }
        }

        private void endPosX_TextChanged(object sender, EventArgs e)
        {

        }

        private void label12_Click(object sender, EventArgs e)
        {

        }
    }
}



