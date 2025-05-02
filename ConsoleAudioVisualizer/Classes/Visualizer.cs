using System.Collections.Concurrent;
using FftSharp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ConsoleAudioVisualizer.Classes
{
    public static class Visualizer
    {
        // Device selection (Unused unless your audio isn't coming through your default windows device, which if it isn't you've probably fucked something up)
        private static MMDevice _audioDevice;
        public static void SelectDevice()
        {
            Console.Clear();

            var devices = new MMDeviceEnumerator()
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToDictionary(d => d.FriendlyName, d => d);

            int count = 0;
            foreach(var choice in devices)
            {
                Console.WriteLine($"{count}: {choice}");
                count++;
            }
            Console.WriteLine("Select a device (default is 0): ");
            string? input = Console.ReadLine();
            if(string.IsNullOrEmpty(input)) return; // No input, use default
            else if (!int.TryParse(input, out int choice)) return; // Invalid input, use default
            else if (choice < 0 || choice >= devices.Count) return; // Out of range, use default
            else if (choice == 0) return; // Default, use default
            else if (choice > 0 && choice < devices.Count) // Valid input, use that device
                _audioDevice = devices.ElementAt(choice).Value;
            else
                return; // Invalid input, use default
        }

        // Capture + Processing Windows WASAPI Loopback
        private static WasapiLoopbackCapture _capture;                                              // Windows Audio API caputure object
        private static ConcurrentQueue<double[]> _frameQueue = new();                               // Frame Queue, each frame of frequency ranges to show

        private const int FftSize = 32_768;                                                         // Window size. MUST BE A POWER OF TWO! FFT does not like anything else
        private static double[] _samples = new double[FftSize];                                     // Samples obtained from WASAPI
        private static System.Numerics.Complex[] _spectrum = new System.Numerics.Complex[FftSize];  // Frequency Spectrumn, unbinned
        private static double[] _magnitudes = new double[FftSize / 2];                              // Sample magnitudes
        private static FftSharp.Windows.Hanning _window = new();                                    // Windowing object from FftSharp

        private static double _maxMagnitude = double.MinValue;
        private static double _minMagnitude = double.MaxValue;

        // Call this to start showing the visualizer
        public static void Show()
        {
            Console.Clear();

            // Use chosen device if one was picked; otherwise default
            _capture = _audioDevice is null
                ? new WasapiLoopbackCapture()
                : new WasapiLoopbackCapture(_audioDevice);

            _capture.DataAvailable += CaptureOnDataAvailable; // Hook Capture function to event that's called when data is ready
            _capture.StartRecording(); // Start Recording

            // Start a new thread for display so main can be left to manage control
            var displayThread = new Thread(Display) { IsBackground = true };
            displayThread.Start();

            // Speaking of control, hold the thread hostage until ESC is hit.
            while (Console.ReadKey(intercept: true).Key != ConsoleKey.Escape) { }

            // Clean up if we do ESCape
            _capture.StopRecording();
            Console.Clear();
        }

        // Audio Capture hook, called whenever _capture has data availible
        private static void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
        {
            // Wave format info from WASAPI (Caputed audio comes in as WAV/Wave format but may be 16/32bit and 44.1/48/96/192khz)
            var wf = _capture.WaveFormat;
            int bytesPerSample = wf.BitsPerSample / 8;
            int bytesPerFrame = bytesPerSample * wf.Channels;
            int totalFrames = e.BytesRecorded / bytesPerFrame;

            // Get our buffers ready and figure out how much to copy
            var wb = new WaveBuffer(e.Buffer);
            int framesToCopy = Math.Min(totalFrames, FftSize);
            int src = (totalFrames - framesToCopy) * bytesPerFrame;

            // Start coping data over from raw into Samples. Don't ask me how this workds, weird computer audio math.
            int i = 0;
            for (; i < framesToCopy; i++, src += bytesPerFrame)
            {
                double mono = bytesPerSample switch
                {
                    2 =>  // 16-bit PCM
                        (wb.ShortBuffer[src >> 1] +
                         (wf.Channels == 2 ? wb.ShortBuffer[(src >> 1) + 1] : wb.ShortBuffer[src >> 1]))
                        * 0.5 / 32768.0,

                    _ => // 32-bit float
                        (wb.FloatBuffer[src >> 2] +
                         (wf.Channels == 2 ? wb.FloatBuffer[(src >> 2) + 1] : wb.FloatBuffer[src >> 2]))
                        * 0.5
                };

                _samples[i] = mono;
            }

            Array.Fill(_samples, 0.0, i, FftSize - i);      // zero-pad incase we dont meet size (remember, must be power of 2)

            // remove DC offset and then window
            double mean = _samples.Average();
            for (int n = 0; n < FftSize; n++) _samples[n] -= mean;
            _window.ApplyInPlace(_samples);

            // Fourier time :D
            _spectrum = FFT.Forward(_samples);
            _magnitudes = FFT.Magnitude(_spectrum);

            // Take our magnitudes and find the actual min and max
            foreach (double m in _magnitudes)
            {
                _maxMagnitude = Math.Max(_maxMagnitude, m);
                _minMagnitude = Math.Min(_minMagnitude, m);
            }

            BuildFrame(_magnitudes, wf.SampleRate);
        }

        // Frame builder. This makes our visualizer frames!
        private static void BuildFrame(double[] magnitudes, int sampleRate)
        {
            // Frequency ranges to show. This is a logarithmic scale, so it will show more low frequencies than high.
            const double fMin = 20;
            double fMax = sampleRate / 2.0;
            int rows = Console.WindowHeight - 1;

            // Logarithmic binning
            double logMin = Math.Log10(fMin);
            double logMax = Math.Log10(fMax);
            double logStep = (logMax - logMin) / rows;

            // Ready our frame buffer
            var frame = new double[rows];
            magnitudes[0] = magnitudes[1] = 0;               // suppress DC

            // Bin magnitudes into availible rows
            for (int bin = 1; bin < magnitudes.Length; bin++)
            {
                double freq = bin * sampleRate / (double)FftSize;
                if (freq <= fMin || freq > fMax) continue;

                int row = (int)((Math.Log10(freq) - logMin) / logStep);
                if (row >= rows) row = rows - 1;

                frame[row] += magnitudes[bin] * magnitudes[bin]; // power
            }

            // Normalize the frame
            // This is done by taking the RMS of each row and scaling it to a 0-1 range
            const double floorDb = -100;
            for (int r = 0; r < rows; r++)
            {
                double rms = Math.Sqrt(frame[r] / (1e-20 + FftSize / rows));
                double db = 20 * Math.Log10(rms + 1e-20);
                frame[r] = Math.Clamp((db - floorDb) / -floorDb, 0, 1);
            }

            // Queue the frame up for display!
            _frameQueue.Enqueue(frame);
            while (_frameQueue.Count > 3) _frameQueue.TryDequeue(out _); // If we have too many frames, we are behind! Skip em and try to catch up. (This shouldn't happen unless you're using a slow console renderer or PC)
        }

        // Console Display, called in a separate thread
        private static void Display()
        {
            while (true)
            {
                if (!_frameQueue.TryDequeue(out var frame)) // No frames?
                {
                    // Sleep to prevent CPU spikes, if you do not limit a loop's speed and give it nothing to do,
                    // it will attempt to run the loop as fast as the CPU can go on some systems (Linux typically suffers from this more than windows).
                    Thread.Sleep(16); // Sleep cannot be less than 16, because it is tied to the system's clock frequency (typically 64/s, or 15.625ms).
                    continue;
                }

                int rows = Math.Min(frame.Length, Console.WindowHeight - 1);
                int cols = Math.Max(1, Console.WindowWidth - 2);

                Console.SetCursorPosition(0, 0);

                for (int r = 0; r < rows; r++)
                {
                    int bar = (int)Math.Round(frame[r] * cols);
                    Console.Write(new string('#', bar));
                    Console.WriteLine(new string(' ', cols - bar));
                }
            }
        }
    }
}
