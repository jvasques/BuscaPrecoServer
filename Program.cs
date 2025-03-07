using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Linq;

class ConsultaProduto
{
    public string CodigoBarras { get; set; }
    public string Descricao { get; set; }
    public string Preco { get; set; }
    public DateTime DataHora { get; set; }

    public ConsultaProduto(string codigoBarras, string descricao, string preco, DateTime dataHora)
    {
        CodigoBarras = codigoBarras;
        Descricao = descricao ?? "Não encontrado";
        Preco = preco ?? "N/A";
        DataHora = dataHora;
    }
}

class BuscaPrecoServer
{
    private static int PORT = 6500;
    public static string DATA_FILE = "produtos.txt";
    private static string CONFIG_FILE = "config.ini";
    private static int UPDATE_INTERVAL_MINUTES = 1;
    private static Dictionary<string, string> produtos = new Dictionary<string, string>();
    private static DateTime ultimaModificacao = DateTime.MinValue;
    private readonly ILogger<BuscaPrecoServer> _logger;
    private static readonly string LogDuplicatesPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "LogDuplicates.txt");
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private static readonly List<ConsultaProduto> consultasRealizadas = new List<ConsultaProduto>();

    public BuscaPrecoServer(ILogger<BuscaPrecoServer> logger)
    {
        _logger = logger;
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        CONFIG_FILE = Path.Combine(exeDir ?? AppContext.BaseDirectory, "config.ini");
        DATA_FILE = Path.Combine(exeDir ?? AppContext.BaseDirectory, "produtos.txt");

        CarregarConfiguracoes();
        CarregarProdutos();
    }

    public async Task StartAsync()
    {
        int updateIntervalMs = UPDATE_INTERVAL_MINUTES * 60 * 1000;
        using (var refreshTimer = new System.Threading.Timer(VerificarAlteracoesArquivo, null, 0, updateIntervalMs))
        {
            _logger.LogInformation($"Timer inicializado para verificar alterações a cada {UPDATE_INTERVAL_MINUTES} minuto(s).");

            TcpListener server = null;
            try
            {
                server = new TcpListener(IPAddress.Any, PORT);
                server.Start();
                _logger.LogInformation($"Servidor iniciado na porta {PORT}...");

                while (!_cts.Token.IsCancellationRequested)
                {
                    TcpClient client = await server.AcceptTcpClientAsync();
                    _logger.LogInformation($"Terminal conectado: {client.Client.RemoteEndPoint}");

                    _ = Task.Run(() => HandleClient(client), _cts.Token);
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Erro ao iniciar o servidor: {e.Message}");
            }
            finally
            {
                server?.Stop();
            }
        }
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    private void CarregarConfiguracoes()
    {
        try
        {
            _logger.LogInformation($"Procurando config.ini em: {CONFIG_FILE}");
            if (File.Exists(CONFIG_FILE))
            {
                string[] linhas = File.ReadAllLines(CONFIG_FILE);
                foreach (string linha in linhas)
                {
                    string linhaTrim = linha.Trim();
                    if (linhaTrim.StartsWith("[") || string.IsNullOrEmpty(linhaTrim)) continue;

                    string[] partes = linhaTrim.Split('=');
                    if (partes.Length == 2)
                    {
                        string chave = partes[0].Trim();
                        string valor = partes[1].Trim();

                        switch (chave.ToLower())
                        {
                            case "porta":
                                if (int.TryParse(valor, out int porta))
                                    PORT = porta;
                                break;
                            case "caminhoarquivo":
                                DATA_FILE = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, valor);
                                break;
                            case "tempoatualizacaominutos":
                                if (int.TryParse(valor, out int tempoMinutos) && tempoMinutos > 0)
                                    UPDATE_INTERVAL_MINUTES = tempoMinutos;
                                else
                                    _logger.LogWarning($"Valor inválido para TempoAtualizacaoMinutos: {valor}. Usando padrão de {UPDATE_INTERVAL_MINUTES} minuto(s).");
                                break;
                        }
                    }
                }
                _logger.LogInformation($"Configurações carregadas: Porta={PORT}, Arquivo={DATA_FILE}, Intervalo de Atualização={UPDATE_INTERVAL_MINUTES} minuto(s)");
            }
            else
            {
                _logger.LogWarning($"Arquivo {CONFIG_FILE} não encontrado. Usando padrões.");
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Erro ao carregar config.ini: {e.Message}. Usando padrões.");
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();

            byte[] okCommand = Encoding.ASCII.GetBytes("#ok");
            stream.Write(okCommand, 0, okCommand.Length);
            stream.Flush();

            Thread.Sleep(5000);
            byte[] buffer = new byte[1024];
            if (stream.DataAvailable)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                string response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
                _logger.LogInformation($"Resposta do terminal: {response}");

                if (response.StartsWith("#tc406"))
                {
                    _logger.LogInformation("Comunicação estabelecida com Busca Preço G2 S (TCP).");
                    ProcessarProtocoloTCP(client, stream);
                }
                else if (response.StartsWith("GET"))
                {
                    _logger.LogInformation("Requisição HTTP detectada.");
                    ProcessarProtocoloHTTP(client, stream);
                }
                else
                {
                    _logger.LogWarning("Protocolo desconhecido.");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Erro na conexão com o terminal: {e.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    private void ProcessarProtocoloTCP(TcpClient client, NetworkStream stream)
    {
        byte[] buffer = new byte[255];
        using (var liveTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (client.Connected)
                {
                    byte[] liveCommand = Encoding.ASCII.GetBytes("#alwayslive");
                    stream.Write(liveCommand, 0, liveCommand.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erro ao enviar #live?: {ex.Message}");
            }
        }, null, 0, 15000))
        {
            while (client.Connected && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (stream.DataAvailable)
                    {
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        string comando = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();

                        if (comando.StartsWith("#") && comando.Length > 1)
                        {
                            if (comando == "#live")
                            {
                                _logger.LogInformation("Recebido #live do terminal.");
                                continue;
                            }

                            string codigoBarras = Regex.Replace(comando.Substring(1), "[^0-9]", "");
                            if (string.IsNullOrWhiteSpace(codigoBarras))
                            {
                                continue;
                            }

                            _logger.LogInformation($"Consulta TCP recebida: {codigoBarras}");

                            lock (produtos)
                            {
                                string resposta;
                                string descricao = null;
                                string preco = null;
                                DateTime dataHora = DateTime.Now;
                                if (produtos.ContainsKey(codigoBarras))
                                {
                                    string[] partes = produtos[codigoBarras].Substring(1).Split('|');
                                    descricao = partes[0];
                                    preco = partes[1].Trim();
                                    resposta = $"#{descricao}|R$ {preco}";
                                }
                                else
                                {
                                    resposta = "#nfound";
                                }

                                lock (consultasRealizadas)
                                {
                                    consultasRealizadas.Add(new ConsultaProduto(codigoBarras, descricao, preco, dataHora));
                                }

                                byte[] respostaBytes = Encoding.ASCII.GetBytes(resposta);
                                stream.Write(respostaBytes, 0, respostaBytes.Length);
                                stream.Flush();
                                _logger.LogInformation($"Resposta TCP enviada: {resposta}");
                            }
                        }
                    }
                    Thread.Sleep(100);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Erro ao processar protocolo TCP: {e.Message}");
                    break;
                }
            }
        }
    }

    private void ProcessarProtocoloHTTP(TcpClient client, NetworkStream stream)
    {
        string codigoBarras = ExtrairCodigoBarrasHTTP(stream);
        if (!string.IsNullOrEmpty(codigoBarras))
        {
            _logger.LogInformation($"Consulta HTTP recebida: {codigoBarras}");

            lock (produtos)
            {
                string respostaTCP = produtos.ContainsKey(codigoBarras) ? produtos[codigoBarras] : "#nfound";
                string descricao = null;
                string preco = null;
                DateTime dataHora = DateTime.Now;
                if (produtos.ContainsKey(codigoBarras))
                {
                    string[] partes = respostaTCP.Substring(1).Split('|');
                    descricao = partes[0];
                    preco = partes[1].Trim();
                }

                lock (consultasRealizadas)
                {
                    consultasRealizadas.Add(new ConsultaProduto(codigoBarras, descricao, preco, dataHora));
                }

                string respostaHTTP = ConverterParaFormatoHTTP(respostaTCP);
                string httpResponse = "HTTP/1.1 200 OK\r\n" +
                                     "Content-Type: text/html\r\n" +
                                     $"Content-Length: {respostaHTTP.Length}\r\n" +
                                     "Connection: close\r\n\r\n" +
                                     respostaHTTP;

                byte[] respostaBytes = Encoding.ASCII.GetBytes(httpResponse);
                stream.Write(respostaBytes, 0, respostaBytes.Length);
                stream.Flush();
                _logger.LogInformation($"Resposta HTTP enviada: {respostaHTTP}");
            }
        }
        else
        {
            string erroResponse = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            byte[] erroBytes = Encoding.ASCII.GetBytes(erroResponse);
            stream.Write(erroBytes, 0, erroBytes.Length);
            stream.Flush();
        }
    }

    private string ExtrairCodigoBarrasHTTP(NetworkStream stream)
    {
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
        var match = Regex.Match(request, @"codEan=([^&]+)");
        return match.Success ? Regex.Replace(match.Groups[1].Value, "[^0-9]", "") : null;
    }

    private string ConverterParaFormatoHTTP(string respostaTCP)
    {
        if (respostaTCP == "#nfound")
            return "<body>Produto não encontrado||||</body>";
        else
        {
            string[] partes = respostaTCP.Substring(1).Split('|');
            string nome = partes[0].Trim();
            string preco = partes[1].Trim();
            return $"<body>{nome}||||{preco}|</body>";
        }
    }

    public void CarregarProdutos()
    {
        try
        {
            _logger.LogInformation($"Procurando produtos em: {DATA_FILE}");
            if (File.Exists(DATA_FILE))
            {
                string[] linhas = File.ReadAllLines(DATA_FILE);
                var novosProdutos = new Dictionary<string, string>();

                foreach (string linha in linhas)
                {
                    string[] partes = linha.Split('|');
                    if (partes.Length == 3)
                    {
                        string codigo = partes[0].Trim();
                        string nome = partes[1].Trim();
                        string precoStr = partes[2].Trim();

                        if (string.Equals(codigo, "SEM GTIN", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning($"Produto com código 'SEM GTIN' ignorado: '{nome}|{precoStr}'.");
                            continue;
                        }

                        string produto = $"#{nome}|{precoStr}";
                        if (decimal.TryParse(precoStr, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out decimal precoDecimal))
                        {
                            string preco = precoDecimal.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
                            produto = $"#{nome}|{preco}";
                        }

                        if (novosProdutos.ContainsKey(codigo))
                        {
                            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Código: {codigo} - Substituído: '{novosProdutos[codigo]}' por '{produto}'";
                            File.AppendAllText(LogDuplicatesPath, logMessage + Environment.NewLine);
                            _logger.LogWarning($"Código duplicado encontrado: {codigo}. Registrado em LogDuplicates.txt.");
                        }
                        novosProdutos[codigo] = produto;
                    }
                }

                lock (produtos)
                {
                    produtos.Clear();
                    foreach (var item in novosProdutos)
                    {
                        produtos[item.Key] = item.Value;
                    }
                    ultimaModificacao = File.GetLastWriteTime(DATA_FILE);
                }

                string produtosLocalPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "produtos.txt");
                using (var writer = new StreamWriter(produtosLocalPath))
                {
                    foreach (var item in novosProdutos)
                    {
                        string[] partes = item.Value.Substring(1).Split('|');
                        writer.WriteLine($"{item.Key}|{partes[0]}|{partes[1]}");
                    }
                }
                _logger.LogInformation($"produtos.txt atualizado com {novosProdutos.Count} produtos em: {produtosLocalPath}");

                _logger.LogInformation($"Produtos carregados: {novosProdutos.Count} (Última modificação: {ultimaModificacao})");
            }
            else
            {
                _logger.LogWarning($"Arquivo {DATA_FILE} não encontrado. Tentando carregar produtos.txt local.");
                string produtosLocalPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "produtos.txt");
                if (File.Exists(produtosLocalPath))
                {
                    CarregarProdutosDeFallback(produtosLocalPath);
                }
                else
                {
                    _logger.LogWarning($"Arquivo local {produtosLocalPath} também não encontrado.");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Erro ao carregar arquivo de produtos: {e.Message}");
        }
    }

    private void CarregarProdutosDeFallback(string caminho)
    {
        try
        {
            string[] linhas = File.ReadAllLines(caminho);
            var novosProdutos = new Dictionary<string, string>();

            foreach (string linha in linhas)
            {
                string[] partes = linha.Split('|');
                if (partes.Length == 3)
                {
                    string codigo = partes[0].Trim();
                    string nome = partes[1].Trim();
                    string precoStr = partes[2].Trim();

                    if (string.Equals(codigo, "SEM GTIN", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning($"Produto com código 'SEM GTIN' ignorado: '{nome}|{precoStr}'.");
                        continue;
                    }

                    string produto = $"#{nome}|{precoStr}";
                    if (decimal.TryParse(precoStr, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out decimal precoDecimal))
                    {
                        string preco = precoDecimal.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
                        produto = $"#{nome}|{preco}";
                    }

                    if (novosProdutos.ContainsKey(codigo))
                    {
                        string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Código: {codigo} - Substituído: '{novosProdutos[codigo]}' por '{produto}'";
                        File.AppendAllText(LogDuplicatesPath, logMessage + Environment.NewLine);
                        _logger.LogWarning($"Código duplicado encontrado: {codigo}. Registrado em LogDuplicates.txt.");
                    }
                    novosProdutos[codigo] = produto;
                }
            }

            lock (produtos)
            {
                produtos.Clear();
                foreach (var item in novosProdutos)
                {
                    produtos[item.Key] = item.Value;
                }
                ultimaModificacao = File.GetLastWriteTime(caminho);
            }

            _logger.LogInformation($"Produtos carregados do fallback: {novosProdutos.Count} (Última modificação: {ultimaModificacao})");
        }
        catch (Exception e)
        {
            _logger.LogError($"Erro ao carregar produtos do fallback {caminho}: {e.Message}");
        }
    }

    private void VerificarAlteracoesArquivo(object state)
    {
        try
        {
            DateTime timeNow = DateTime.Now;
            _logger.LogInformation($"Verificando alterações no arquivo de produtos em {timeNow:yyyy-MM-dd HH:mm:ss}...");
            if (File.Exists(DATA_FILE))
            {
                DateTime modificacaoAtual = File.GetLastWriteTime(DATA_FILE);
                _logger.LogInformation($"Última modificação registrada: {ultimaModificacao}, Modificação atual: {modificacaoAtual}");

                if (modificacaoAtual > ultimaModificacao)
                {
                    _logger.LogInformation("Arquivo de produtos alterado. Recarregando...");
                    CarregarProdutos();
                }
                else
                {
                    _logger.LogInformation("Nenhuma alteração detectada no arquivo de produtos.");
                }
            }
            else
            {
                _logger.LogWarning($"Arquivo {DATA_FILE} não encontrado.");
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"Erro ao verificar alterações no arquivo: {e.Message}");
        }
    }

    public static (List<(string CodigoBarras, string Descricao, string Preco, int Quantidade)> Consultas, DateTime? DataInicio, DateTime? DataFim) GetConsultasAgrupadas()
    {
        lock (consultasRealizadas)
        {
            if (consultasRealizadas.Count == 0)
            {
                return (new List<(string, string, string, int)>(), null, null);
            }

            var agrupadas = consultasRealizadas
                .GroupBy(c => c.CodigoBarras)
                .Select(g => (
                    CodigoBarras: g.Key,
                    Descricao: g.First().Descricao,
                    Preco: g.First().Preco,
                    Quantidade: g.Count()
                ))
                .ToList();

            var dataInicio = consultasRealizadas.Min(c => c.DataHora);
            var dataFim = consultasRealizadas.Max(c => c.DataHora);

            return (agrupadas, dataInicio, dataFim);
        }
    }
}

class LogForm : Form
{
    private TextBox _logTextBox;
    private Button _exportButton;
    private Button _forceUpdateButton;
    private bool _isInitialized = false;
    private BuscaPrecoServer _server; // Mudança para não readonly, permitindo atualização

    public LogForm() // Construtor sem parâmetros para inicialização inicial
    {
        InitializeComponents();
    }

    public void SetServer(BuscaPrecoServer server) // Método para definir o servidor posteriormente
    {
        _server = server;
    }

    private void InitializeComponents()
    {
        Text = "Busca Preço Server - Logs";
        Size = new Size(600, 400);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;

        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BuscaPrecoServer.barcodeOn.ico"))
        {
            if (stream != null)
            {
                Icon = new Icon(stream);
            }
        }

        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 35
        };

        _exportButton = new Button
        {
            Text = "Exportar Consultas",
            Location = new Point(10, 5),
            Size = new Size(120, 25)
        };
        _exportButton.Click += ExportButton_Click;

        _forceUpdateButton = new Button
        {
            Text = "Forçar Atualização",
            Location = new Point(140, 5),
            Size = new Size(120, 25)
        };
        _forceUpdateButton.Click += ForceUpdateButton_Click;

        panel.Controls.Add(_exportButton);
        panel.Controls.Add(_forceUpdateButton);

        _logTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10)
        };

        Controls.Add(_logTextBox);
        Controls.Add(panel);
        Load += (s, e) => _isInitialized = true;
    }

    private void ExportButton_Click(object sender, EventArgs e)
    {
        try
        {
            var (consultasAgrupadas, dataInicio, dataFim) = BuscaPrecoServer.GetConsultasAgrupadas();
            if (consultasAgrupadas.Count == 0)
            {
                MessageBox.Show("Nenhuma consulta registrada.", "Exportar Consultas", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string exportPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, $"Consultas_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            using (var writer = new StreamWriter(exportPath))
            {
                writer.WriteLine($"Relatório de Consultas - Gerado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Consultas realizadas de {dataInicio:dd/MM/yyyy HH:mm:ss} até {dataFim:dd/MM/yyyy HH:mm:ss}");
                writer.WriteLine("Código de Barras | Descrição | Preço | Quantidade de Consultas");
                writer.WriteLine("---------------------------------------------------------------");
                foreach (var consulta in consultasAgrupadas.OrderByDescending(c => c.Quantidade))
                {
                    writer.WriteLine($"{consulta.CodigoBarras} | {consulta.Descricao} | {consulta.Preco} | {consulta.Quantidade}");
                }
                writer.WriteLine($"Total de consultas únicas: {consultasAgrupadas.Count}");
                writer.WriteLine($"Total de consultas realizadas: {consultasAgrupadas.Sum(c => c.Quantidade)}");
            }

            MessageBox.Show($"Consultas exportadas com sucesso para: {exportPath}", "Exportar Consultas", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao exportar consultas: {ex.Message}", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ForceUpdateButton_Click(object sender, EventArgs e)
    {
        if (_server == null)
        {
            MessageBox.Show("Servidor não inicializado ainda.", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _server.CarregarProdutos();
            MessageBox.Show("Atualização dos produtos forçada com sucesso.", "Forçar Atualização", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao forçar atualização: {ex.Message}", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void AppendLog(string message)
    {
        if (!_isInitialized) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }
        _logTextBox.AppendText(message + Environment.NewLine);
        _logTextBox.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}

class TextBoxLoggerProvider : ILoggerProvider
{
    private readonly LogForm _logForm;

    public TextBoxLoggerProvider(LogForm logForm)
    {
        _logForm = logForm;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TextBoxLogger(_logForm);
    }

    public void Dispose() { }
}

class TextBoxLogger : ILogger
{
    private readonly LogForm _logForm;

    public TextBoxLogger(LogForm logForm)
    {
        _logForm = logForm;
    }

    public IDisposable BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        string message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
        if (exception != null)
            message += $"\nException: {exception}";

        Console.WriteLine(message);
        _logForm?.AppendLog(message); // Verificação de nulidade
    }
}

class Program6
{
    private static BuscaPrecoServer _server;
    private static NotifyIcon _notifyIcon;
    private static LogForm _logForm;
    private static System.Threading.Timer _iconTimer;
    private static Icon[] _icons;
    private static int _iconIndex = 0;

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Criar o LogForm primeiro
        _logForm = new LogForm();

        // Configurar o logger com o LogForm
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new TextBoxLoggerProvider(_logForm));
            builder.SetMinimumLevel(LogLevel.Information);
        });
        ILogger<BuscaPrecoServer> logger = loggerFactory.CreateLogger<BuscaPrecoServer>();

        // Criar o servidor
        _server = new BuscaPrecoServer(logger);

        // Definir o servidor no LogForm
        _logForm.SetServer(_server);

        // Exibir e ocultar o LogForm
        _logForm.Show();
        _logForm.Hide();

        _icons = new Icon[2];
        using (var onStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BuscaPrecoServer.barcodeOn.ico"))
        using (var offStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BuscaPrecoServer.barcodeOff.ico"))
        {
            if (onStream != null && offStream != null)
            {
                _icons[0] = new Icon(onStream);
                _icons[1] = new Icon(offStream);
            }
            else
            {
                _icons[0] = SystemIcons.Application;
                _icons[1] = SystemIcons.Application;
            }
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons[0],
            Text = "Busca Preço Server",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Exibir Logs", null, (s, e) => ShowLogForm());
        contextMenu.Items.Add("Sair", null, (s, e) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowLogForm();

        _iconTimer = new System.Threading.Timer(_ =>
        {
            _iconIndex = (_iconIndex + 1) % _icons.Length;
            _notifyIcon.Icon = _icons[_iconIndex];
        }, null, 0, 1000);

        Task.Run(() => _server.StartAsync());

        Application.Run();
    }

    private static void ShowLogForm()
    {
        if (_logForm.Visible)
        {
            _logForm.WindowState = FormWindowState.Normal;
            _logForm.Activate();
        }
        else
        {
            _logForm.Show();
        }
    }

    private static void ExitApplication()
    {
        _server.Stop();
        _iconTimer.Dispose();
        _notifyIcon.Visible = false;
        foreach (var icon in _icons)
        {
            icon.Dispose();
        }
        _notifyIcon.Dispose();
        _logForm.Close();
        Application.Exit();
    }
}