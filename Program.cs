using System.Diagnostics;
using System.Text;

namespace EvsConsole;

class Program
{
    static readonly int[] PrimaryModeToRate =
        [2800, 7200, 8000, 9600, 13200, 16400, 24400, 32000, 48000, 64000, 96000, 128000, 2400, -1, 0, 0];

    static readonly int[] AmrwbIoModeToRate =
        [6600, 8850, 12650, 14250, 15850, 18250, 19850, 23050, 23850, 1750, -1, -1, -1, -1, 0, 0];

    // EVS supported bitrates for encoding (bps)
    static readonly int[] EvsBitrates =
        [7200, 8000, 9600, 13200, 16400, 24400, 32000, 48000, 64000, 96000, 128000];

    static string _decoderPath = null!;
    static string? _encoderPath = null;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) { PrintUsage(); return 0; }

        int sampleRateKhz = 8;
        string? decoderPath = null;
        bool quiet = false;
        bool keepTemp = false;
        bool probeMode = false;
        bool encodeMode = false;
        int encodeBitrate = 24400; // default 24.4 kbps
        string? encodeFormat = null; // "g192" or "mime"
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
                case "--encoder" or "-e":
                    if (++i >= args.Length) { Error("Не указан путь"); return 1; }
                    _encoderPath = args[i];
                    break;
                case "--encode" or "-c":
                    encodeMode = true;
                    // Check if next arg is bitrate
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var br))
                    {
                        encodeBitrate = br;
                        i++;
                    }
                    break;
                case "--bitrate" or "-b":
                    if (++i >= args.Length) { Error("Не указан bitrate"); return 1; }
                    if (!int.TryParse(args[i], out encodeBitrate) || Array.IndexOf(EvsBitrates, encodeBitrate) < 0)
                    { Error($"Bitrate: {string.Join(", ", EvsBitrates)}"); return 1; }
                    break;
                case "--format":
                    if (++i >= args.Length) { Error("Не указан формат"); return 1; }
                    encodeFormat = args[i].ToLowerInvariant();
                    if (encodeFormat is not ("g192" or "mime"))
                    { Error("Формат: g192 или mime"); return 1; }
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

        // Encode mode: WAV -> EVS
        if (encodeMode)
        {
            _encoderPath ??= FindEncoder();
            if (!File.Exists(_encoderPath)) { Error($"Энкодер не найден: {_encoderPath}"); return 1; }
            if (!quiet) Console.WriteLine($"Encoder: {_encoderPath}");

            string? outPath = positional.Count > 1 ? positional[1] : null;
            if (Directory.Exists(inputPath))
                return EncodeDirectory(inputPath, outPath, sampleRateKhz, encodeBitrate, encodeFormat, quiet, keepTemp);

            outPath ??= Path.ChangeExtension(inputPath, ".evs");
            return EncodeFile(inputPath, outPath, sampleRateKhz, encodeBitrate, encodeFormat, quiet, keepTemp);
        }

        // Decode mode: EVS -> WAV
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

    const int TocResyncMaxSkip = 256;
    const int TocResyncMinConfirmFrames = 5;

    /// <summary>Правила MIME storage для ToC-байта (после очистки H/F), см. 3GPP decoder bitstream.c.</summary>
    static bool IsMimeTocCompliant(byte storedToc)
    {
        if ((storedToc & 0xC0) != 0) return false;
        if ((storedToc & 0x20) == 0)
            return (storedToc & 0x30) == 0; /* EVS primary: зарезервированные биты 4–5 должны быть 0 */
        return true; /* AMR-WB IO: достаточно валидного mode из таблицы */
    }

    /// <summary>Читает один ToC-фрейм с текущего смещения (без ресинхронизации).</summary>
    static bool TryReadTocFrame(byte[] data, int offset, out byte toc, out int bitrate, out int dataBytes)
    {
        toc = 0;
        bitrate = 0;
        dataBytes = 0;
        if (offset >= data.Length) return false;
        toc = data[offset];
        byte storedToc = (byte)(toc & 0x3F);
        if (!IsMimeTocCompliant(storedToc)) return false;
        bool isAMRWB = (storedToc & 0x20) != 0;
        int mode = storedToc & 0x0F;
        bitrate = isAMRWB ? AmrwbIoModeToRate[mode] : PrimaryModeToRate[mode];
        if (bitrate < 0) return false;
        int numBits = bitrate / 50;
        dataBytes = (numBits + 7) / 8;
        if (offset + 1 + dataBytes > data.Length) return false;
        return true;
    }

    /// <summary>Проверяет, что с позиции <paramref name="start"/> подряд парсятся минимум N ToC-фреймов.</summary>
    static bool HasConsecutiveTocFrames(byte[] data, int start, int minFrames)
    {
        int off = start;
        for (int i = 0; i < minFrames; i++)
        {
            if (!TryReadTocFrame(data, off, out _, out _, out int db)) return false;
            off += 1 + db;
        }
        return true;
    }

    /// <summary>Сколько байт подряд удаётся разобрать как ToC-поток, начиная с <paramref name="start"/>.</summary>
    static int CountTocParseableBytes(byte[] data, int start)
    {
        int off = start, total = 0;
        while (off < data.Length)
        {
            if (!TryReadTocFrame(data, off, out _, out _, out int db)) break;
            total += 1 + db;
            off += 1 + db;
        }
        return total;
    }

    /// <summary>
    /// Парсит файл с ToC-байтами (EVS MIME без magic word, возможно с F-битом).
    /// Формат: [ToC][data][ToC][data]...
    /// ToC byte: |H|F|E|x|mode(4bit)|
    ///   H,F — игнорируем (могут быть установлены из RTP)
    ///   E — 0=EVS Primary, 1=AMR-WB IO
    ///   mode — индекс в PRIMARYmode2rate или AMRWB_IOmode2rate
    /// Поддерживается короткий «разрыв» между сегментами (буфер/склейка записей): ищется следующий валидный поток.
    /// </summary>
    static (int Frames, List<(int Offset, byte Toc, int Bitrate, int DataBytes)> FrameInfo) ParseTocFrames(byte[] data)
    {
        var frames = new List<(int Offset, byte Toc, int Bitrate, int DataBytes)>();
        int offset = 0;

        while (offset < data.Length)
        {
            byte toc = data[offset];
            byte storedToc = (byte)(toc & 0x3F);
            bool isAMRWB = (storedToc & 0x20) != 0;
            int mode = storedToc & 0x0F;
            int bitrate = isAMRWB ? AmrwbIoModeToRate[mode] : PrimaryModeToRate[mode];

            if (bitrate < 0 || !IsMimeTocCompliant(storedToc))
            {
                int bestSkipAny = -1;
                int bestParsedAny = 0;
                int bestSkipEofPrimary = -1;
                int bestCandEofPrimary = int.MaxValue;

                for (int skip = 1; skip <= TocResyncMaxSkip && offset + skip < data.Length; skip++)
                {
                    int cand = offset + skip;
                    if (!HasConsecutiveTocFrames(data, cand, TocResyncMinConfirmFrames)) continue;
                    int n = CountTocParseableBytes(data, cand);
                    if (n > bestParsedAny)
                    {
                        bestParsedAny = n;
                        bestSkipAny = skip;
                    }

                    // Предпочитаем старт с Primary 24.4k (0x46 & 0x3F == 0x06), который забирает весь хвост до EOF —
                    // иначе «максимум байт» часто выбирает ложное выравнивание внутри мусора между сегментами.
                    if (cand + n == data.Length && (data[cand] & 0x3F) == 0x06 && cand < bestCandEofPrimary)
                    {
                        bestCandEofPrimary = cand;
                        bestSkipEofPrimary = skip;
                    }
                }

                int useSkip = bestSkipEofPrimary >= 0 ? bestSkipEofPrimary : bestSkipAny;
                if (useSkip < 0) break;
                offset += useSkip;
                continue;
            }

            int numBits = bitrate / 50;
            int numBytes = (numBits + 7) / 8;
            if (offset + 1 + numBytes > data.Length) break;

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

    static List<List<(int Offset, byte Toc, int Bitrate, int DataBytes)>> SplitTocFramesOnGaps(
        List<(int Offset, byte Toc, int Bitrate, int DataBytes)> frames)
    {
        var result = new List<List<(int Offset, byte Toc, int Bitrate, int DataBytes)>>();
        if (frames.Count == 0) return result;

        var cur = new List<(int Offset, byte Toc, int Bitrate, int DataBytes)> { frames[0] };
        for (int i = 1; i < frames.Count; i++)
        {
            var prev = frames[i - 1];
            int expectedNext = prev.Offset + 1 + prev.DataBytes;
            if (frames[i].Offset != expectedNext)
            {
                result.Add(cur);
                cur = new List<(int Offset, byte Toc, int Bitrate, int DataBytes)>();
            }
            cur.Add(frames[i]);
        }
        result.Add(cur);
        return result;
    }

    /// <summary>
    /// MIME storage: magic + каналы + [ToC][payload]… для подмножества фреймов (один непрерывный кусок исходного файла).
    /// </summary>
    static byte[] BuildMimeFromFrames(byte[] input, List<(int Offset, byte Toc, int Bitrate, int DataBytes)> frames)
    {
        byte[] magic = Encoding.ASCII.GetBytes("#!EVS_MC1.0\n");
        byte[] channels = [0x00, 0x00, 0x00, 0x01];
        int headerLen = magic.Length + channels.Length;
        int outputSize = headerLen;
        foreach (var f in frames)
            outputSize += 1 + f.DataBytes;

        var output = new byte[outputSize];
        magic.CopyTo(output, 0);
        channels.CopyTo(output, magic.Length);
        int pos = headerLen;
        foreach (var f in frames)
        {
            output[pos++] = (byte)(f.Toc & 0x3F);
            Array.Copy(input, f.Offset + 1, output, pos, f.DataBytes);
            pos += f.DataBytes;
        }
        return output;
    }

    static void PrintTocFrameStats(int frameCount, List<(int Offset, byte Toc, int Bitrate, int DataBytes)> frames)
    {
        var bitrateStats = new Dictionary<int, int>();
        foreach (var f in frames)
            bitrateStats[f.Bitrate] = bitrateStats.GetValueOrDefault(f.Bitrate) + 1;

        double duration = frameCount * 0.02;
        Console.WriteLine($"Фреймов: {frameCount} ({duration:F1}с)");
        foreach (var kv in bitrateStats.OrderByDescending(x => x.Value))
        {
            string label = kv.Key == 0 ? "NO_DATA/LOST" : $"{kv.Key} bps";
            Console.WriteLine($"  {label}: {kv.Value} фреймов");
        }
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
        var (frameCount, frames) = ParseTocFrames(inputData);
        if (frameCount == 0)
            throw new Exception("Не удалось распарсить ToC фреймы");

        var segments = SplitTocFramesOnGaps(frames);
        if (!quiet)
        {
            if (segments.Count > 1)
                Console.WriteLine($"ToC: {segments.Count} непрерывных сегмента (между ними разрыв в исходном файле)");
            PrintTocFrameStats(frameCount, frames);
        }

        if (segments.Count == 1)
            return RunDecoder(BuildMimeFromFrames(inputData, segments[0]), sampleRateKhz, isMime: true, keepTemp);

        using var pcmOut = new MemoryStream();
        foreach (var seg in segments)
        {
            byte[] mime = BuildMimeFromFrames(inputData, seg);
            byte[] pcm = RunDecoder(mime, sampleRateKhz, isMime: true, keepTemp);
            pcmOut.Write(pcm, 0, pcm.Length);
        }
        return pcmOut.ToArray();
    }

    #endregion

    #region Encode files

    static int EncodeDirectory(string inputDir, string? outputDir, int sampleRateKhz, int bitrate, string? format, bool quiet, bool keepTemp)
    {
        var files = Directory.GetFiles(inputDir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".wav")
            .ToList();

        if (files.Count == 0) { Error($"Нет WAV файлов в {inputDir}"); return 1; }

        outputDir ??= inputDir;
        Directory.CreateDirectory(outputDir);

        int ok = 0, fail = 0;
        foreach (var file in files)
        {
            string evsPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".evs");
            Console.WriteLine($"\n--- {Path.GetFileName(file)} ---");
            if (EncodeFile(file, evsPath, sampleRateKhz, bitrate, format, quiet, keepTemp) == 0) ok++; else fail++;
        }
        Console.WriteLine($"\nИтого: {ok} OK, {fail} ошибок из {files.Count}");
        return fail > 0 ? 1 : 0;
    }

    static int EncodeFile(string inputPath, string outputPath, int sampleRateKhz, int bitrate, string? format, bool quiet, bool keepTemp)
    {
        if (!File.Exists(inputPath)) { Error($"Файл не найден: {inputPath}"); return 1; }

        byte[] inputData = File.ReadAllBytes(inputPath);
        if (!quiet)
        {
            Console.WriteLine($"Вход: {inputPath} ({inputData.Length:N0} байт)");
        }

        if (inputData.Length < 44) { Error("Файл слишком маленький"); return 1; }

        try
        {
            string wavFormat = DetectWavFormat(inputData);
            if (!quiet) Console.WriteLine($"Формат: {wavFormat}");

            if (wavFormat != "wav-pcm")
                throw new Exception($"Поддерживается только WAV PCM формат. Используйте --probe для анализа.");

            var (pcmData, wavSampleRate, wavChannels) = ParseWavFile(inputData);

            // Validate sample rate
            if (wavSampleRate is not (8000 or 16000 or 32000 or 48000))
                throw new Exception($"Неподдерживаемая частота дискретизации: {wavSampleRate} Гц. Поддерживаются: 8000, 16000, 32000, 48000");

            // Use input sample rate if not explicitly set (default 8 means auto-detect)
            int fs = sampleRateKhz == 8 ? wavSampleRate / 1000 : sampleRateKhz;

            // Downmix to mono if stereo
            if (wavChannels > 1)
            {
                if (!quiet) Console.WriteLine($"Downmix {wavChannels} каналов -> моно");
                pcmData = DownmixToMono(pcmData, wavChannels);
            }

            bool useMime = format == "mime";
            byte[] evsData = RunEncoder(pcmData, fs, bitrate, useMime, keepTemp);

            File.WriteAllBytes(outputPath, evsData);

            double duration = (double)pcmData.Length / (wavSampleRate * 2);
            Console.WriteLine($"Выход: {outputPath} ({evsData.Length:N0} байт, {TimeSpan.FromSeconds(duration):mm\\:ss\\.f})");
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception ex) { Error(ex.Message); return 1; }
    }

    /// <summary>
    /// Detects WAV PCM format by checking for "RIFF" header
    /// </summary>
    static string DetectWavFormat(byte[] data)
    {
        if (data.Length >= 12)
        {
            string riff = Encoding.ASCII.GetString(data, 0, 4);
            string wave = Encoding.ASCII.GetString(data, 8, 4);
            if (riff == "RIFF" && wave == "WAVE")
                return "wav-pcm";
        }
        return "unknown";
    }

    /// <summary>
    /// Parses WAV file header and extracts PCM data, sample rate, and channel count
    /// </summary>
    static (byte[] PcmData, int SampleRate, int Channels) ParseWavFile(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        // RIFF header
        string riff = new string(br.ReadChars(4));
        if (riff != "RIFF") throw new Exception("Не RIFF файл");

        uint fileSize = br.ReadUInt32();
        string wave = new string(br.ReadChars(4));
        if (wave != "WAVE") throw new Exception("Не WAV файл");

        // Find "fmt " chunk
        while (ms.Position < data.Length - 8)
        {
            string chunkId = new string(br.ReadChars(4));
            uint chunkSize = br.ReadUInt32();

            if (chunkId == "fmt ")
            {
                short audioFormat = br.ReadInt16();
                if (audioFormat != 1) throw new Exception("Поддерживается только PCM формат (audioFormat=1)");

                short channels = br.ReadInt16();
                int sampleRate = br.ReadInt32();
                br.ReadUInt32(); // byte rate
                br.ReadUInt16(); // block align
                short bitsPerSample = br.ReadInt16();

                if (bitsPerSample != 16) throw new Exception($"Поддерживается только 16-bit PCM, получено: {bitsPerSample}");

                // Skip to "data" chunk
                // Remaining bytes in fmt chunk
                long remainingInChunk = chunkSize - 16;
                if (remainingInChunk > 0) br.ReadBytes((int)remainingInChunk);

                // Now find "data" chunk
                while (ms.Position < data.Length - 8)
                {
                    string dataChunkId = new string(br.ReadChars(4));
                    uint dataSize = br.ReadUInt32();

                    if (dataChunkId == "data")
                    {
                        // 0xFFFFFFFF means "unknown size" (streaming WAV) — read to end
                        int bytesToRead = (dataSize == 0xFFFFFFFF) ? (int)(data.Length - ms.Position) : (int)dataSize;
                        byte[] pcmData = br.ReadBytes(bytesToRead);
                        return (pcmData, sampleRate, channels);
                    }
                    else
                    {
                        // Skip this chunk
                        br.ReadBytes((int)dataSize);
                    }
                }

                throw new Exception("Не найден 'data' чанк в WAV файле");
            }
            else
            {
                // Skip this chunk
                br.ReadBytes((int)chunkSize);
            }
        }

        throw new Exception("Не найден 'fmt ' чанк в WAV файле");
    }

    /// <summary>
    /// Downmix multi-channel PCM to mono by averaging channels
    /// </summary>
    static byte[] DownmixToMono(byte[] pcmData, int channels)
    {
        if (channels == 1) return pcmData;

        int sampleCount = pcmData.Length / 2;
        int monoSamples = sampleCount / channels;
        var mono = new short[monoSamples];

        for (int i = 0; i < monoSamples; i++)
        {
            int sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += BitConverter.ToInt16(pcmData, (i * channels + ch) * 2);
            }
            mono[i] = (short)(sum / channels);
        }

        var bytes = new byte[monoSamples * 2];
        Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Runs EVS encoder (EVS_cod.exe) and returns encoded bitstream
    /// </summary>
    static byte[] RunEncoder(byte[] pcmData, int sampleRateKhz, int bitrate, bool useMime, bool keepTemp)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "EvsConsole");
        Directory.CreateDirectory(tmpDir);

        string id = Guid.NewGuid().ToString("N")[..8];
        string inputFile = Path.Combine(tmpDir, $"{id}.{sampleRateKhz}k");
        string outputFile = Path.Combine(tmpDir, $"{id}{(useMime ? ".evs" : ".192")}");

        try
        {
            File.WriteAllBytes(inputFile, pcmData);

            var arguments = useMime
                ? $"-mime -q {bitrate} {sampleRateKhz} \"{inputFile}\" \"{outputFile}\""
                : $"-q {bitrate} {sampleRateKhz} \"{inputFile}\" \"{outputFile}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _encoderPath!,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi) ?? throw new Exception("Не удалось запустить энкодер");

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
                throw new Exception($"Encoder exit {proc.ExitCode}: {err}");
            }

            if (!File.Exists(outputFile)) throw new Exception("Encoder produced no output");
            return File.ReadAllBytes(outputFile);
        }
        finally
        {
            if (!keepTemp) { TryDelete(inputFile); TryDelete(outputFile); }
            else { Console.WriteLine($"  Temp: {inputFile}"); Console.WriteLine($"  Temp: {outputFile}"); }
        }
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

    static string FindEncoder()
    {
        // First try to find EVS_cod.exe (reference encoder name)
        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string path = Path.Combine(dir, "EVS_cod.exe");
            if (File.Exists(path)) return path;
        }
        // Then try evs_enc.exe
        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string path = Path.Combine(dir, "evs_enc.exe");
            if (File.Exists(path)) return path;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "EVS_cod.exe");
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
        EVS Codec Converter (EVS <-> WAV)
        ===================================

        Конвертирует EVS аудиофайлы в WAV и обратно (WAV в EVS).
        Автоматически определяет формат: EVS-ToC, MIME, G.192, WAV PCM.

        Использование:
          # Декодирование (EVS -> WAV):
          EvsConsole <вход.evs/bin> [выход.wav] [опции]
          EvsConsole <папка> [папка_выход]

          # Кодирование (WAV -> EVS):
          EvsConsole --encode <вход.wav> [выход.evs] [опции]
          EvsConsole --encode <папка_wav> [папка_выход] [опции]

          # Анализ файла:
          EvsConsole --probe <файл>

        Опции декодирования:
          -s, --samplerate <кГц>    Частота выхода: 8, 16, 32, 48 (по умолч.: 8)
          -w, --whisper             Формат для Whisper: 16 кГц, моно, PCM 16-bit
          -d, --decoder <путь>      Путь к EVS_dec.exe

        Опции кодирования:
          -c, --encode [bitrate]    Режим кодирования WAV -> EVS
          -b, --bitrate <bps>       Битрейт: 7200, 8000, 9600, 13200, 16400, 24400,
                                    32000, 48000, 64000, 96000, 128000 (по умолч.: 24400)
              --format <g192|mime>  Формат выхода: g192 (по умолч.) или mime
          -e, --encoder <путь>      Путь к EVS_cod.exe

        Общие опции:
          -p, --probe               Анализ структуры файла
          -q, --quiet               Минимальный вывод
              --keep-temp           Не удалять временные файлы
          -h, --help                Справка

        Входной формат для кодирования:
          WAV PCM 16-bit, mono или stereo (автоматически downmix в mono)
          Частота: 8000, 16000, 32000 или 48000 Гц

        Для подготовки WAV через ffmpeg:
          ffmpeg -i input.mp3 -ar 16000 -c:a pcm_s16le -ac 1 output.wav

        Поддерживаемые форматы декодирования (автоопределение):
          evs-toc  — EVS с ToC байтами (из RTP/BLOB, переменный bitrate)
          mime     — EVS MIME Storage Format (#!EVS_MC1.0)
          g192     — ITU G.192 bitstream (sync word 0x6B21)

        Примеры:
          # Декодирование
          EvsConsole data_file.bin
          EvsConsole data_file.bin output.wav -s 16
          EvsConsole data_file.bin --whisper          (16 кГц для Whisper)
          EvsConsole --probe data_file.bin
          EvsConsole recordings_folder/ output_folder/

          # Кодирование
          EvsConsole --encode audio.wav
          EvsConsole --encode audio.wav output.evs -b 32000
          EvsConsole --encode audio.wav output.evs --format mime
          EvsConsole --encode recordings/ output_evs/
        """);

    #endregion
}
