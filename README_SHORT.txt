EvsConsole.exe 1.bin => на выходе 1.wav (декодирование)
EvsConsole.exe 1.wav 1.evs --encode --format mime --bitrate 8000
EvsConsole.exe 1.wav 1.evs --encode --format toc -b 24400
  => toc: поток [ToC][data]… как в Sprut (MIME без заголовка, DTX по умолчанию)