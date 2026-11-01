using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace G_write
{
    /// <summary>
    /// Универсальный интерфейс подключения к ЧПУ контроллеру (COM-порт или TCP Wi-Fi).
    /// Позволяет остальному коду отправлять команды одинаково вне зависимости от типа подключения.
    /// </summary>
    public interface ICNCConnection : IDisposable
    {
        bool IsOpen { get; }
        void Open();
        void Close();
        void Write(string text);
        void WriteLine(string text);
        void Write(byte[] buffer, int offset, int count);
        event EventHandler<string>? LineReceived;
        event EventHandler? Disconnected;
    }

    /// <summary>
    /// Реализация подключения к контроллеру через Serial (USB COM-порт).
    /// КРОССПЛАТФОРМЕННЫЕ ОСОБЕННОСТИ:
    /// - На Windows порты именуются как COM1, COM2, COM10...
    /// - На Linux порты устройств находятся в /dev/ttyUSB*, /dev/ttyACM* и /dev/serial/by-id/*.
    /// </summary>
    public class SerialCNCConnection : ICNCConnection
    {
        private readonly string _portName;
        private readonly int _baudRate;
        private SerialPort? _serialPort;
        private bool _isDisposed;

        public event EventHandler<string>? LineReceived;
        public event EventHandler? Disconnected;

        public bool IsOpen => _serialPort != null && _serialPort.IsOpen;

        public SerialCNCConnection(string portName, int baudRate = 115200)
        {
            _portName = portName;
            _baudRate = baudRate;
        }

        /// <summary>
        /// Сканирует и возвращает список доступных последовательных портов с учетом специфики ОС.
        /// </summary>
        public static string[] GetAvailablePorts()
        {
            var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Стандартное API .NET
                foreach (var p in SerialPort.GetPortNames())
                {
                    if (!string.IsNullOrWhiteSpace(p))
                        ports.Add(p);
                }
            }
            catch { }

            // Для Linux сканируем файловую систему /dev/ для обнаружения USB-Serial чипов (CH340, FTDI, CP2102)
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    if (Directory.Exists("/dev"))
                    {
                        foreach (var f in Directory.GetFiles("/dev", "ttyUSB*"))
                            ports.Add(f);
                        foreach (var f in Directory.GetFiles("/dev", "ttyACM*"))
                            ports.Add(f);
                    }
                    if (Directory.Exists("/dev/serial/by-id"))
                    {
                        foreach (var f in Directory.GetFiles("/dev/serial/by-id"))
                            ports.Add(f);
                    }
                }
                catch { }
            }

            var list = new List<string>(ports);
            list.Sort();
            return list.ToArray();
        }

        public void Open()
        {
            if (IsOpen) return;

            // 115200 бод, 8 бит данных, без четности, 1 стоп-бит (стандарт для Inectra MSC-3US и GRBL)
            _serialPort = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
            {
                NewLine = "\n",
                ReadTimeout = 2000,
                WriteTimeout = 2000
            };

            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                while (_serialPort.IsOpen && _serialPort.BytesToRead > 0)
                {
                    string line = _serialPort.ReadLine();
                    LineReceived?.Invoke(this, line);
                }
            }
            catch (TimeoutException) { }
            catch (Exception)
            {
                if (!_isDisposed && (_serialPort == null || !_serialPort.IsOpen))
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void Write(string text)
        {
            if (!IsOpen || _serialPort == null) return;
            try
            {
                _serialPort.Write(text);
            }
            catch
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }

        public void WriteLine(string text)
        {
            if (!IsOpen || _serialPort == null) return;
            try
            {
                _serialPort.WriteLine(text);
            }
            catch
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!IsOpen || _serialPort == null) return;
            try
            {
                _serialPort.Write(buffer, offset, count);
            }
            catch
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }

        public void Close()
        {
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnDataReceived;
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                Close();
            }
        }
    }

    /// <summary>
    /// Реализация подключения к контроллеру через беспроводную сеть Wi-Fi (TCP-сокет к мосту ESP-32S).
    /// </summary>
    public class TcpCNCConnection : ICNCConnection
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _readTask;
        private readonly object _writeLock = new object();
        private bool _isDisposed;

        public event EventHandler<string>? LineReceived;
        public event EventHandler? Disconnected;

        public bool IsOpen => _client != null && _client.Connected && _stream != null;

        public TcpCNCConnection(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public void Open()
        {
            if (IsOpen) return;

            _client = new TcpClient
            {
                // NoDelay = true отключает алгоритм Нагла для мгновенной отправки каждого байта без буферизации
                NoDelay = true,
                SendTimeout = 3000,
                ReceiveTimeout = 0
            };

            // Подключение с таймаутом 4 секунды (чтобы интерфейс не висел)
            var connectTask = _client.ConnectAsync(_host, _port);
            if (!connectTask.Wait(TimeSpan.FromSeconds(4)))
            {
                _client.Close();
                _client = null;
                throw new TimeoutException($"Не удалось подключиться к {_host}:{_port} (тайм-аут ожидания).");
            }

            _stream = _client.GetStream();
            _cts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) return;

            byte[] buffer = new byte[2048];
            var lineBuilder = new StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested && IsOpen)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0)
                    {
                        // Удаленный мост ESP-32S закрыл сокет
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        char c = (char)buffer[i];
                        if (c == '\n')
                        {
                            string line = lineBuilder.ToString().TrimEnd('\r');
                            lineBuilder.Clear();
                            if (line.Length > 0)
                            {
                                LineReceived?.Invoke(this, line);
                            }
                        }
                        else
                        {
                            lineBuilder.Append(c);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                if (!_isDisposed)
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void Write(string text)
        {
            if (!IsOpen || _stream == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                lock (_writeLock)
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
            }
            catch
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }

        public void WriteLine(string text)
        {
            if (!IsOpen || _stream == null) return;
            try
            {
                if (!text.EndsWith("\n"))
                {
                    text += "\n";
                }
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                lock (_writeLock)
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
            }
            catch
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!IsOpen || _stream == null) return;
            try
            {
                lock (_writeLock)
                {
                    _stream.Write(buffer, offset, count);
                    _stream.Flush();
                }
            }
            catch
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                throw;
            }
        }

        public void Close()
        {
            try
            {
                _cts?.Cancel();
                _stream?.Close();
                _stream?.Dispose();
                _stream = null;
                _client?.Close();
                _client?.Dispose();
                _client = null;
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                Close();
            }
        }
    }
}
