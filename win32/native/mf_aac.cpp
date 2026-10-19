/*
 * Media Foundation AAC/M4A/WMA decoder for TrpgAudio.dll.
 *
 * WHY: miniaudio has no AAC decoder, so .m4a / .aac / .wma would not open.
 * Windows itself has shipped an AAC-LC decoder since Windows 7 (as a Media
 * Foundation transform), so instead of bundling yet another library we ask the
 * OS to do it. No licensing question, no extra bytes in the package.
 *
 * HOW IT IS USED: audio_bridge.c tries miniaudio's built-in decoders first
 * (wav / mp3 / flac / ogg) and only falls back to this when those fail. So the
 * existing formats keep the exact same code path and this can only add
 * formats, never change the ones that already work.
 *
 * The whole file is decoded into interleaved 16-bit PCM in memory. That costs
 * about 10 MB per minute of stereo 44.1 kHz audio - fine for a music track,
 * and it keeps this layer small (a streaming data_source would be ~2x the
 * code for the same audible result).
 *
 * C interface (see mf_aac.h), so the C side needs no C++ and no COM knowledge.
 *
 * ASCII-only comments: MSVC reads a BOM-less file as ANSI, and this machine's
 * ANSI code page is GBK.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "mf_aac.h"

/* ------------------------------------------------------------------ errors */

static char g_error[512];

static void set_error(const char* what, HRESULT hr)
{
    _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "%s (HRESULT 0x%08X)",
                what, (unsigned)hr);
    g_error[sizeof(g_error) - 1] = '\0';
}

static void set_error_text(const char* text)
{
    _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "%s", text);
    g_error[sizeof(g_error) - 1] = '\0';
}

static const char* mf_error_text(void)
{
    return g_error[0] ? g_error : "Media Foundation: unknown error";
}

const char* trpg_mf_last_error(void)
{
    return mf_error_text();
}

/* --------------------------------------------------------------- MF startup */

/* MFStartup/CoInitializeEx are process-wide; do them once, lazily.
   CoInitializeEx may legitimately fail with RPC_E_CHANGED_MODE when the host
   thread already joined an STA (our C# UI thread is STA) - that is fine, the
   source reader still works. */
static int g_mfReady = 0;
static int g_mfStarted = 0;

static int ensure_mf(void)
{
    HRESULT hr;

    if (g_mfReady) return 1;

    if (!g_mfStarted) {
        hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
        if (hr != S_OK && hr != S_FALSE && hr != RPC_E_CHANGED_MODE) {
            set_error("CoInitializeEx failed", hr);
            return 0;
        }

        hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
        if (FAILED(hr)) {
            set_error("MFStartup failed", hr);
            return 0;
        }
        g_mfStarted = 1;
    }

    g_mfReady = 1;
    g_error[0] = '\0';
    return 1;
}

/* --------------------------------------------------------------- open a file */

/*
 * Open a file and force the output to 16-bit PCM at the file's own rate and
 * channel count. Media Foundation inserts its AAC/MP3/WMA decoder as needed -
 * we never look at the compressed format at all.
 */
static IMFSourceReader* open_reader(const wchar_t* path, int* channels,
                                    int* sampleRate, long long* frames)
{
    IMFSourceReader* reader = NULL;
    IMFMediaType* pcmType = NULL;
    IMFMediaType* actual = NULL;
    PROPVARIANT duration;
    HRESULT hr;
    UINT32 value = 0;

    if (!ensure_mf()) return NULL;

    hr = MFCreateSourceReaderFromURL(path, NULL, &reader);
    if (FAILED(hr)) {
        set_error("cannot open (no decoder for this format?)", hr);
        if (reader != NULL) reader->Release();
        return NULL;
    }

    /* Tell MF we want plain PCM. The decoder + resampler chain is built for us. */
    hr = MFCreateMediaType(&pcmType);
    if (FAILED(hr)) { set_error("MFCreateMediaType failed", hr); goto fail; }

    pcmType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    pcmType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);

    hr = reader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                                     NULL, pcmType);
    if (FAILED(hr)) {
        set_error("this file has no decodable audio stream", hr);
        goto fail;
    }

    hr = reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM, &actual);
    if (FAILED(hr)) { set_error("GetCurrentMediaType failed", hr); goto fail; }

    actual->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &value);
    *channels = (int)value;
    value = 0;
    actual->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &value);
    *sampleRate = (int)value;

    if (*channels <= 0 || *sampleRate <= 0) {
        set_error_text("decoder reported a bogus channel count or sample rate");
        goto fail;
    }

    /* Duration, in 100 ns ticks -> sample frames. */
    PropVariantInit(&duration);
    *frames = 0;
    hr = reader->GetPresentationAttribute((DWORD)MF_SOURCE_READER_MEDIASOURCE,
                                          MF_PD_DURATION, &duration);
    if (SUCCEEDED(hr) && duration.vt == VT_UI8) {
        *frames = (long long)((double)duration.uhVal.QuadPart / 10000000.0 * *sampleRate);
    }
    PropVariantClear(&duration);

    if (pcmType != NULL) pcmType->Release();
    if (actual != NULL) actual->Release();
    return reader;

fail:
    if (pcmType != NULL) pcmType->Release();
    if (actual != NULL) actual->Release();
    if (reader != NULL) reader->Release();
    return NULL;
}

/* ---------------------------------------------------------------- decode all */

int trpg_mf_decode_file(const wchar_t* path, void** ppPcm, long long* pFrames,
                        int* pChannels, int* pSampleRate, char* err, int errCap)
{
    IMFSourceReader* reader;
    short* buffer = NULL;
    long long capacity = 0;      /* frames (per channel) that fit in buffer */
    long long written = 0;       /* frames decoded so far */
    int channels = 0;
    int sampleRate = 0;
    long long expected = 0;

    if (err != NULL && errCap > 0) err[0] = '\0';
    if (ppPcm == NULL || path == NULL) {
        if (err != NULL) _snprintf_s(err, errCap, _TRUNCATE, "bad arguments");
        return 0;
    }

    reader = open_reader(path, &channels, &sampleRate, &expected);
    if (reader == NULL) {
        if (err != NULL) _snprintf_s(err, errCap, _TRUNCATE, "%s", mf_error_text());
        return 0;
    }

    /* Start a bit above the reported duration; grow if the stream runs long. */
    capacity = expected > 0 ? expected + sampleRate : (long long)sampleRate * 60;
    buffer = (short*)malloc((size_t)(capacity * channels * 2));
    if (buffer == NULL) {
        reader->Release();
        if (err != NULL) _snprintf_s(err, errCap, _TRUNCATE, "out of memory");
        return 0;
    }

    for (;;) {
        DWORD flags = 0;
        LONGLONG timestamp = 0;
        DWORD streamIndex = 0;
        IMFSample* sample = NULL;
        HRESULT hr;

        hr = reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                                &streamIndex, &flags, &timestamp, &sample);
        if (FAILED(hr)) {
            /* Truncated file: keep whatever we already decoded. */
            if (sample != NULL) sample->Release();
            break;
        }

        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) {
            if (sample != NULL) sample->Release();
            break;
        }
        if (sample == NULL) continue;      /* gap / format change, try again */

        {
            IMFMediaBuffer* mediaBuffer = NULL;
            BYTE* data = NULL;
            DWORD length = 0;

            hr = sample->ConvertToContiguousBuffer(&mediaBuffer);
            if (SUCCEEDED(hr)) {
                hr = mediaBuffer->Lock(&data, NULL, &length);
                if (SUCCEEDED(hr)) {
                    long long framesInSample = (long long)(length / (channels * 2));

                    if (written + framesInSample > capacity) {
                        long long grown = (capacity * 2) + sampleRate;
                        short* resized = (short*)realloc(buffer,
                                            (size_t)(grown * channels * 2));
                        if (resized == NULL) {
                            mediaBuffer->Unlock();
                            mediaBuffer->Release();
                            sample->Release();
                            goto done;
                        }
                        buffer = resized;
                        capacity = grown;
                    }

                    memcpy(buffer + (size_t)(written * channels), data,
                           (size_t)length);
                    written += framesInSample;

                    mediaBuffer->Unlock();
                }
                mediaBuffer->Release();
            }
        }

        sample->Release();
    }

done:
    reader->Release();

    if (written <= 0) {
        free(buffer);
        if (err != NULL) {
            _snprintf_s(err, errCap, _TRUNCATE,
                        "%s", g_error[0] ? g_error : "decoded zero frames");
        }
        return 0;
    }

    *ppPcm = buffer;
    *pFrames = written;
    *pChannels = channels;
    *pSampleRate = sampleRate;
    g_error[0] = '\0';
    return 1;
}

void trpg_mf_free(void* p)
{
    if (p != NULL) free(p);
}

/*
 * Cheap check used by the library scanner: can this file be opened and
 * negotiated to PCM? No decoding happens, so it costs milliseconds.
 */
int trpg_mf_can_decode(const wchar_t* path)
{
    IMFSourceReader* reader;
    int channels = 0;
    int sampleRate = 0;
    long long frames = 0;

    reader = open_reader(path, &channels, &sampleRate, &frames);
    if (reader == NULL) return 0;

    reader->Release();
    g_error[0] = '\0';
    return 1;
}
