using System.Diagnostics;
using System.Text;

namespace EvsConsole;

class Program
{
    static readonly int[] PrimaryModeToRate =
        [2800, 7200, 8000, 9600, 13200, 16400, 24400, 32000, 48000, 64000, 96000, 128000, 2400, -1, 0, 0];

    static readonly int[] AmrwbIoModeToRate =
        [6600, 8850, 12650, 14250, 15850, 18250, 19850, 23050, 23850, 1750, -1, -1, -1, -1, 0, 0];

    static string _decoderPath = null!;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) { PrintUsage(); return 0; }

        int sampleRateKhz = 8;
        string? decoderPath = null;
        bool quiet = false;
        bool keepTemp = false;
        bool probeMode = false;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--samplerate" or "-s":
                    if (++i >= args.Length) { Error("Не указано значение"); return 1; }
                    if (!int.TryParse(args[i], out sampleRateKhz) || sampleRateKhz is not (8 or 16 or 32 or 48))
                    { Error("Sample rate: 8, 16, 32 или 48"); return 1; }
                    break;
                case "--decoder" or "-d":
                    if (++i >= args.Length) { Error("Не указан путь"); return 1; }
                    decoderPath = args[i];
                    break;
                case "-w" or "--whisper": sampleRateKhz = 16; break;
                case "-q" or "--quiet": quiet = true; break;
                case "--keep-temp": keepTemp = true; break;
                case "--probe" or "-p": probeMode = true; break;
                case "-h" or "--help": PrintUsage(); return 0;
                default: positional.Add(args[i]); break;
            }
        }

        if (positional.Count == 0) { Error("Не указан входной файл."); return 1; }
        string inputPath = positional[0];

        if (probeMode) return ProbeFile(inputPath);

        _decoderPath = decoderPath ?? FindDecoder();
        if (!File.Exists(_decoderPath)) { Error($"Декодер не найден: {_decoderPath}"); return 1; }

        if (!quiet) Console.WriteLine($"Decoder: {_decoderPath}");

        string? outputPath = positional.Count > 1 ? positional[1] : null;

        if (Directory.Exists(inputPath))
            return ProcessDirectory(inputPath, outputPath, sampleRateKhz, quiet, keepTemp);

        outputPath ??= Path.ChangeExtension(inputPath, ".wav");
        return ProcessFile(inputPath, outputPath, sampleRateKhz, quiet, keepTemp);
    }

    #region ToC Parsing — ключевая логика

    /// <summary>
    /// Парсит файл с ToC-байтами (EVS MIME без magic word, возможно с F-битом).
    /// Формат: [ToC][data][ToC][data]...
    /// ToC byte: |H|F|E|x|mode(4bit)|
    ///   H,F — игнорируем (могут быть установлены из RTP)
    ///   E — 0=EVS Primary, 1=AMR-WB IO
    ///   mode — индекс в PRIMARYmode2rate или AMRWB_IOmode2rate
    /// </summary>
    static (int Frames, List<(int Offset, byte Toc, int Bitrate, int DataBytes)> FrameInfo) ParseTocFrames(byte[] data)
    {
        var frames = new List<(int Offset, byte Toc, int Bitrate, int DataBytes)>();
        int offset = 0;

        while (offset < data.Length)
        {
            byte toc = data[offset];
            byte cleanToc = (byte)(toc & 0x3F); // Clear H and F bits

            bool isAMRWB = (cleanToc & 0x20) != 0;
            int mode = cleanToc & 0x0F;

            int bitrate = isAMRWB ? AmrwbIoModeToRate[mode] : PrimaryModeToRate[mode];
            if (bitrate < 0) break; // invalid mode → stop

            int numBits = bitrate / 50;
            int numBytes = (numBits + 7) / 8;

            if (offset + 1 + numBytes > data.Length) break; // truncated

            frames.Add((offset, toc, bitrate, numBytes));
            offset += 1 + numBytes;
        }

        return (frames.Count, frames);
    }

    /// <summary>
    /// Проверяет, является ли файл EVS ToC форматом.
    /// Валидация: первые несколько фреймов должны парситься корректно,
    /// и общая длительность должна быть разумной.
    /// </summary>
    static bool IsTocFormat(byte[] data)
    {
        if (data.Length < 10) return false;

        var (frameCount, frames) = ParseTocFrames(data);
        if (frameCount < 10) return false;

        int totalParsed = 0;
        foreach (var f in frames) totalParsed += 1 + f.DataBytes;

        // Фреймы должны покрывать не менее 80% файла
        if (totalParsed < data.Length * 0.8) return false;

        // Хотя бы 50% фреймов должны быть с bitrate > 0
        int voicedFrames = frames.Count(f => f.Bitrate > 0);
        if (voicedFrames < frameCount * 0.3) return false;

        return true;
    }

    /// <summary>
    /// Создаёт корректный EVS MIME файл: magic word + исправленные ToC байты + данные.
    /// Очищает биты H и F в каждом ToC байте.
    /// </summary>
    static byte[] TocToMime(byte[] input, out int frameCount, bool quiet)
    {
        var (count, frames) = ParseTocFrames(input);
        frameCount = count;

        if (count == 0)
            throw new Exception("Не удалось распарсить ToC фреймы");

        byte[] magic = Encoding.ASCII.GetBytes("#!EVS_MC1.0\n");
        byte[] channels = [0x00, 0x00, 0x00, 0x01]; // 1 channel, required by decoder
        int headerLen = magic.Length + channels.Length;
        int outputSize = headerLen;
        foreach (var f in frames)
            outputSize += 1 + f.DataBytes;

        var output = new byte[outputSize];
        magic.CopyTo(output, 0);
        channels.CopyTo(output, magic.Length);
        int pos = headerLen;

        var bitrateStats = new Dictionary<int, int>();
        foreach (var f in frames)
        {
            byte fixedToc = (byte)(f.Toc & 0x3F); // Clear H and F bits
            output[pos++] = fixedToc;
            Array.Copy(input, f.Offset + 1, output, pos, f.DataBytes);
            pos += f.DataBytes;

            bitrateStats[f.Bitrate] = bitrateStats.GetValueOrDefault(f.Bitrate) + 1;
        }

        if (!quiet)
        {
            double duration = count * 0.02;
            Console.WriteLine($"Фреймов: {count} ({duration:F1}с)");
            foreach (var kv in bitrateStats.OrderByDescending(x => x.Value))
            {
                string label = kv.Key == 0 ? "NO_DATA/LOST" : $"{kv.Key} bps";
                Console.WriteLine($"  {label}: {kv.Value} фреймов");
            }
        }

        return output;
    }

    #endregion

    #region Process files

    static int ProcessDirectory(string inputDir, string? outputDir, int sampleRateKhz, bool quiet, bool keepTemp)
    {
        var files = Directory.GetFiles(inputDir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is not (".wav" or ".pcm" or ".txt" or ".md" or ".exe" or ".dll"))
            .ToList();

        if (files.Count == 0) { Error($"Нет файлов в {inputDir}"); return 1; }

        outputDir ??= inputDir;
        Directory.CreateDirectory(outputDir);

        int ok = 0, fail = 0;
        foreach (var file in files)
        {
            string wavPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".wav");
            Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");
            if (ProcessFile(file, wavPath, sampleRateKhz, quiet, keepTemp) == 0) ok++; else fail++;
        }
        Console.WriteLine($"\nИтого: {ok} OK, {fail} ошибок из {files.Count}");
        return fail > 0 ? 1 : 0;
    }

    static int ProcessFile(string inputPath, string outputPath, int sampleRateKhz, bool quiet, bool keepTemp)
    {
        if (!File.Exists(inputPath)) { Error($"Файл не найден: {inputPath}"); return 1; }

        byte[] inputData = File.ReadAllBytes(inputPath);
        if (!quiet)
        {
            Console.WriteLine($"Вход: {inputPath} ({inputData.Length:N0} байт)");
            PrintHexDump(inputData, 48);
        }

        if (inputData.Length < 4) { Error("Файл слишком маленький"); return 1; }

        byte[]? pcmData;
        try
        {
            string format = DetectFormat(inputData);
            if (!quiet) Console.WriteLine($"Формат: {format}");

            pcmData = format switch
            {
                "evs-toc" => DecodeEvsToc(inputData, sampleRateKhz, quiet, keepTemp),
                "mime" => RunDecoder(inputData, sampleRateKhz, isMime: true, keepTemp),
                "g192" => RunDecoder(inputData, sampleRateKhz, isMime: false, keepTemp),
                _ => throw new Exception($"Не удалось определить формат файла. Используйте --probe для анализа.")
            };
        }
        catch (Exception ex) { Error(ex.Message); return 1; }

        if (pcmData is not { Length: > 0 }) { Error("Декодирование не дало результата"); return 1; }

        byte[] wavData = PcmToWav(pcmData, sampleRateKhz * 1000);
        File.WriteAllBytes(outputPath, wavData);

        double duration = (double)pcmData.Length / (sampleRateKhz * 1000 * 2);
        Console.WriteLine($"Выход: {outputPath} ({wavData.Length:N0} байт, {TimeSpan.FromSeconds(duration):mm\\:ss\\.f})");
        Console.WriteLine("OK");
        return 0;
    }

    static byte[] DecodeEvsToc(byte[] inputData, int sampleRateKhz, bool quiet, bool keepTemp)
    {
        byte[] mimeData = TocToMime(inputData, out int frameCount, quiet);
        return RunDecoder(mimeData, sampleRateKhz, isMime: true, keepTemp);
    }

    #endregion

    #region Format Detection

    static string DetectFormat(byte[] data)
    {
        if (data.Length >= 15)
        {
            string header = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 15));
            if (header.StartsWith("#!EVS_MC1.0") || header.StartsWith("#!AMR-WB"))
                return "mime";
        }

        if (data.Length >= 4)
        {
            ushort syncWord = BitConverter.ToUInt16(data, 0);
            if (syncWord is 0x6B21 or 0x6B20)
            {
                ushort numBits = BitConverter.ToUInt16(data, 2);
                if (numBits * 50 <= 128000)
                    return "g192";
            }
        }

        if (IsTocFormat(data))
            return "evs-toc";

        return "unknown";
    }

    #endregion

    #region Probe

    static int ProbeFile(string inputPath)
    {
        if (!File.Exists(inputPath)) { Error($"Файл не найден: {inputPath}"); return 1; }
        byte[] data = File.ReadAllBytes(inputPath);

        Console.WriteLine($"Файл:   {inputPath}");
        Console.WriteLine($"Размер: {data.Length:N0} байт");
        Console.WriteLine();

        int dumpLen = Math.Min(data.Length, 128);
        for (int row = 0; row < dumpLen; row += 16)
        {
            var sb = new StringBuilder($"  {row,4:X4}: ");
            for (int col = 0; col < 16 && row + col < dumpLen; col++)
                sb.Append($"{data[row + col]:X2} ");
            Console.WriteLine(sb);
        }

        Console.WriteLine();
        string format = DetectFormat(data);
        Console.WriteLine($"Формат: {format}");

        if (format == "evs-toc")
        {
            Console.WriteLine("\n=== EVS ToC фреймы ===");
            var (count, frames) = ParseTocFrames(data);

            var stats = new Dictionary<int, int>();
            foreach (var f in frames)
                stats[f.Bitrate] = stats.GetValueOrDefault(f.Bitrate) + 1;

            Console.WriteLine($"  Всего фреймов: {count}");
            Console.WriteLine($"  Длительность:  {count * 0.02:F1}с");
            foreach (var kv in stats.OrderByDescending(x => x.Value))
            {
                string label = kv.Key == 0 ? "NO_DATA/LOST" : $"{kv.Key} bps";
                Console.WriteLine($"  {label}: {kv.Value} фреймов");
            }

            Console.WriteLine($"\n  Первые фреймы:");
            foreach (var f in frames.Take(10))
            {
                var hexData = new StringBuilder();
                int showBytes = Math.Min(8, f.DataBytes);
                for (int j = 0; j < showBytes; j++)
                    hexData.Append($"{data[f.Offset + 1 + j]:X2} ");
                Console.WriteLine($"    @{f.Offset,6}: ToC=0x{f.Toc:X2} → {f.Bitrate,6} bps ({f.DataBytes}B) data: {hexData}...");
            }

            Console.WriteLine($"\nКонвертация:");
            Console.WriteLine($"  EvsConsole \"{inputPath}\"");
        }

        return 0;
    }

    #endregion

    #region EVS Decoder

    static byte[] RunDecoder(byte[] data, int sampleRateKhz, bool isMime, bool keepTemp)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "EvsConsole");
        Directory.CreateDirectory(tmpDir);

        string id = Guid.NewGuid().ToString("N")[..8];
        string inputFile = Path.Combine(tmpDir, $"{id}{(isMime ? ".evs" : ".192")}");
        string outputFile = Path.Combine(tmpDir, $"{id}.{sampleRateKhz}k");

        try
        {
            File.WriteAllBytes(inputFile, data);

            var arguments = isMime
                ? $"-mime -q {sampleRateKhz} \"{inputFile}\" \"{outputFile}\""
                : $"-q {sampleRateKhz} \"{inputFile}\" \"{outputFile}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _decoderPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi) ?? throw new Exception("Не удалось запустить декодер");

            var stderrTask = proc.StandardError.ReadToEndAsync();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();

            if (!proc.WaitForExit(120_000))
            {
                proc.Kill();
                throw new Exception("Timeout 120s");
            }

            string stderr = stderrTask.GetAwaiter().GetResult();

            if (proc.ExitCode != 0)
            {
                string err = string.Join("; ", stderr.Split('\n').Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.Contains("EVS Codec") && !l.StartsWith("===")));
                throw new Exception($"Decoder exit {proc.ExitCode}: {err}");
            }

            if (!File.Exists(outputFile)) throw new Exception("Decoder produced no output");
            byte[] pcm = File.ReadAllBytes(outputFile);
            if (pcm.Length == 0) throw new Exception("Decoder output empty");
            return pcm;
        }
        finally
        {
            if (!keepTemp) { TryDelete(inputFile); TryDelete(outputFile); }
            else { Console.WriteLine($"  Temp: {inputFile}"); Console.WriteLine($"  Temp: {outputFile}"); }
        }
    }

    #endregion

    #region PCM → WAV

    static byte[] PcmToWav(byte[] pcm, int sampleRateHz)
    {
        var wav = new byte[44 + pcm.Length];
        using var ms = new MemoryStream(wav);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8);
        bw.Write(36 + pcm.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRateHz);
        bw.Write(sampleRateHz * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(pcm.Length);
        bw.Write(pcm);
        return wav;
    }

    #endregion

    #region Utilities

    static string FindDecoder()
    {

        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string path = Path.Combine(dir, "evs_dec.exe");
            if (File.Exists(path)) return path;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "evs_dec.exe");
    }

    static void PrintHexDump(byte[] data, int maxBytes)
    {
        int len = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder("  Hex: ");
        for (int i = 0; i < len; i++) { sb.Append($"{data[i]:X2} "); if (i == 15) sb.Append(" "); }
        if (data.Length > maxBytes) sb.Append("...");
        Console.WriteLine(sb);
    }

    static void Error(string msg) => Console.Error.WriteLine($"ОШИБКА: {msg}");
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    static void PrintUsage() => Console.WriteLine("""
        EVS to WAV Converter
        ====================

        Конвертирует EVS аудиофайлы (из Oracle BLOB, RTP потоков и др.) в WAV.
        Автоматически определяет формат: EVS-ToC, MIME, G.192.

        Использование:
          EvsConsole <вход> [выход.wav] [опции]
          EvsConsole <папка> [папка_выход]
          EvsConsole --probe <файл>

        Опции:
          -s, --samplerate <кГц>    Частота выхода: 8, 16, 32, 48 (по умолч.: 8)
          -w, --whisper             Формат для Whisper: 16 кГц, моно, PCM 16-bit
          -d, --decoder <путь>      Путь к EVS_dec.exe
          -p, --probe               Анализ структуры файла
          -q, --quiet               Минимальный вывод
              --keep-temp           Не удалять временные файлы
          -h, --help                Справка

        Поддерживаемые форматы (автоопределение):
          evs-toc  — EVS с ToC байтами (из RTP/BLOB, переменный bitrate)
          mime     — EVS MIME Storage Format (#!EVS_MC1.0)
          g192     — ITU G.192 bitstream (sync word 0x6B21)

        Примеры:
          EvsConsole data_file.bin
          EvsConsole data_file.bin output.wav -s 16
          EvsConsole data_file.bin --whisper          (16 кГц для Whisper)
          EvsConsole --probe data_file.bin
          EvsConsole recordings_folder/ output_folder/
        """);

    #endregion
}
