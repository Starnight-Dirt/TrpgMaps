/*
 * TrpgAudio - native audio backend for the win7 (32-bit) build of TrpgMaps.
 *
 * WHY THIS FILE EXISTS
 * --------------------
 * The win7 build is .NET Framework 3.5 / x86 and must not drag in third-party
 * managed assemblies. It also has to play MP3 / WAV / FLAC / OGG, which the
 * system APIs alone cannot do (MCI can do MP3 and WAV only - it has never had
 * a Vorbis decoder, and it will not open FLAC/AAC).
 *
 * So we ship one small native DLL built from this file plus miniaudio:
 *
 *   miniaudio  - single-header audio library, "public domain or MIT-0"
 *                https://github.com/mackron/miniaudio  (v0.11.25)
 *
 * miniaudio's license is the whole point of choosing it: a project released
 * under MIT may redistribute it freely, unlike e.g. BASS (free for
 * non-commercial use only) or FMOD. See THIRD-PARTY.txt next to this file.
 *
 * WHAT IT DECODES
 * ---------------
 * wav / mp3 / flac  - built into miniaudio
 * ogg vorbis        - via stb_vorbis.c (public domain), see below
 * aac / m4a         - NOT supported: miniaudio has no AAC decoder and we do
 *                     not want to pull in another library for it. Those files
 *                     are listed in the UI but fail to open with a clear
 *                     message, instead of silently doing nothing.
 *
 * Everything is exported as plain C with __cdecl and UTF-16 file names, so the
 * C# side can P/Invoke it directly with no wrapper assembly.
 *
 * NOTE: comments in this file are intentionally ASCII-only. MSVC reads a .c
 * file without a BOM as ANSI, and this machine's ANSI code page is GBK - a
 * UTF-8 comment would decode as garbage (usually harmless, but not always).
 */

/*
 * stb_vorbis provides the Ogg Vorbis decoder. miniaudio does NOT embed one -
 * it only lights up MA_HAS_VORBIS when it sees STB_VORBIS_INCLUDE_STB_VORBIS_H,
 * and it calls stb_vorbis_* directly without declaring them, so stb_vorbis's
 * header has to be visible first.
 *
 * The implementation is pulled in AFTER miniaudio: stb_vorbis's internal macros
 * otherwise leak into windows.h (which miniaudio includes) and it dies with
 * "winnt.h: error C2059: syntax error".
 *
 * License: public domain (or MIT, at your option).
 */
#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENCODING          /* we only ever decode, never encode */
#define MA_NO_GENERATION        /* no waveform generators, no noise/duplex  */
#include "miniaudio.h"

#undef STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#include <stdio.h>
#include <string.h>

#include "mf_aac.h"

#define TRPG_EXPORT __declspec(dllexport)
#define TRPG_CALL   __cdecl

/* One engine and one track per process: this is a music player, not a mixer. */
static ma_engine g_engine;
static ma_sound  g_sound;
static int       g_engineReady = 0;
static int       g_soundReady  = 0;
static float     g_lengthProbe = 0.0f;   /* duration for formats that need it */
static char      g_error[512];

/*
 * Media Foundation path (m4a / aac / wma).
 *
 * miniaudio's built-in decoders handle wav / mp3 / flac / ogg; everything else
 * is decoded by Windows itself (see mf_aac.cpp) into a block of PCM, which we
 * then hand to miniaudio as an in-memory audio buffer. Only one of the two
 * paths is alive at a time - close_sound() tears down both.
 */
static ma_audio_buffer g_buffer;
static int             g_bufferReady = 0;
static void*           g_decodedPcm  = NULL;

/*
 * Duration of an Ogg Vorbis file.
 *
 * Why we need our own: miniaudio feeds stb_vorbis through a push-mode callback,
 * and stb_vorbis cannot report a stream length in that mode, so
 * ma_sound_get_length_in_seconds() returns 0 for .ogg - the progress bar would
 * have nothing to scale against. Opening the file a second time in pull mode
 * (which can seek) answers it in a few milliseconds.
 *
 * stb_vorbis only takes char* and uses fopen(), i.e. the ANSI code page. That
 * handles Chinese file names on a zh-CN system; playback itself goes through
 * ma_sound_init_from_file_w (wide chars) and is unaffected either way.
 */
static float vorbis_length_seconds(const wchar_t* filePath)
{
    char narrow[1024];
    stb_vorbis* vorbis;
    int openError = 0;
    float seconds;

    if (WideCharToMultiByte(CP_ACP, 0, filePath, -1, narrow, (int)sizeof(narrow), NULL, NULL) <= 0)
        return 0.0f;

    vorbis = stb_vorbis_open_filename(narrow, &openError, NULL);
    if (vorbis == NULL) return 0.0f;

    seconds = stb_vorbis_stream_length_in_seconds(vorbis);
    stb_vorbis_close(vorbis);
    return seconds > 0.0f ? seconds : 0.0f;
}

static void set_error(ma_result result, const char* what)
{
    const char* text = ma_result_description(result);
    _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "%s: %s (%d)",
                what, text ? text : "unknown", (int)result);
    g_error[sizeof(g_error) - 1] = '\0';
}

static void set_error_text(const char* text)
{
    _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "%s", text);
    g_error[sizeof(g_error) - 1] = '\0';
}

static void close_sound(void)
{
    g_lengthProbe = 0.0f;

    /* Order matters: the sound references the buffer, the buffer references
       the PCM block. Tear down in that order or miniaudio reads freed memory. */
    if (g_soundReady) {
        ma_sound_stop(&g_sound);
        ma_sound_uninit(&g_sound);
        g_soundReady = 0;
    }
    if (g_bufferReady) {
        ma_audio_buffer_uninit(&g_buffer);
        g_bufferReady = 0;
    }
    if (g_decodedPcm != NULL) {
        trpg_mf_free(g_decodedPcm);
        g_decodedPcm = NULL;
    }
}

/*
 * Fallback path: let Media Foundation decode the whole file, then play it as
 * an in-memory audio buffer.
 *
 * This is deliberately a fallback rather than the default. The built-in
 * decoders stream (a few hundred KB of RAM no matter how long the track is)
 * and are already known good; MF costs ~10 MB per minute because the whole
 * track has to be in memory. Keeping the order "built-in first, MF second"
 * also means this change can only ADD formats, never alter the ones that
 * already worked.
 */
static int open_with_mf(const wchar_t* filePath)
{
    void* pcm = NULL;
    long long frames = 0;
    int channels = 0;
    int sampleRate = 0;
    char error[512];
    ma_audio_buffer_config config;
    ma_result result;

    if (!trpg_mf_decode_file(filePath, &pcm, &frames, &channels, &sampleRate,
                             error, (int)sizeof(error))) {
        set_error_text(error);
        return 0;
    }

    config = ma_audio_buffer_config_init(ma_format_s16, (ma_uint32)channels,
                                         (ma_uint64)frames, pcm, NULL);
    /* v0.11's config_init leaves the rate at 0 and expects the caller to fill
       it in (the header even says so) - forget it and the track plays at the
       device's rate, i.e. wrong pitch. */
    config.sampleRate = (ma_uint32)sampleRate;

    result = ma_audio_buffer_init(&config, &g_buffer);
    if (result != MA_SUCCESS) {
        trpg_mf_free(pcm);
        set_error(result, "wrap the decoded audio");
        return 0;
    }
    g_decodedPcm = pcm;
    g_bufferReady = 1;

    result = ma_sound_init_from_data_source(&g_engine, &g_buffer, 0, NULL, &g_sound);
    if (result != MA_SUCCESS) {
        ma_audio_buffer_uninit(&g_buffer);
        g_bufferReady = 0;
        trpg_mf_free(pcm);
        g_decodedPcm = NULL;
        set_error(result, "play the decoded audio");
        return 0;
    }

    g_soundReady = 1;
    return 1;
}

TRPG_EXPORT const char* TRPG_CALL trpg_audio_version(void)
{
    return "TrpgAudio/miniaudio " MA_VERSION_STRING;
}

TRPG_EXPORT const char* TRPG_CALL trpg_audio_last_error(void)
{
    return g_error[0] ? g_error : "no error";
}

TRPG_EXPORT int TRPG_CALL trpg_audio_init(void)
{
    ma_result result;

    if (g_engineReady) return 1;

    /* NULL config = default device, default sample rate, default everything. */
    result = ma_engine_init(NULL, &g_engine);
    if (result != MA_SUCCESS) {
        set_error(result, "init audio device");
        return 0;
    }

    g_engineReady = 1;
    g_error[0] = '\0';
    return 1;
}

TRPG_EXPORT void TRPG_CALL trpg_audio_shutdown(void)
{
    close_sound();
    if (g_engineReady) {
        ma_engine_uninit(&g_engine);
        g_engineReady = 0;
    }
}

/*
 * Open a file and park it at 0:00 (nothing plays until trpg_audio_play).
 *
 * MA_SOUND_FLAG_STREAM decodes on the fly instead of unpacking the whole track
 * into RAM - a 4 minute MP3 would otherwise cost ~40 MB of PCM.
 */
TRPG_EXPORT int TRPG_CALL trpg_audio_open(const wchar_t* filePath)
{
    ma_result result;

    if (filePath == NULL || filePath[0] == L'\0') {
        set_error_text("empty file path");
        return 0;
    }

    if (!trpg_audio_init()) return 0;
    close_sound();

    result = ma_sound_init_from_file_w(&g_engine, filePath,
                                       MA_SOUND_FLAG_STREAM,
                                       NULL, NULL, &g_sound);
    if (result != MA_SUCCESS) {
        /* The built-in decoders (wav / mp3 / flac / ogg) could not open it, so
           hand it to Windows' own decoder - that is what makes .m4a / .aac /
           .wma work. Note the order: this is a *fallback*, so the formats that
           already played keep exactly the same code path. */
        if (open_with_mf(filePath)) {
            g_error[0] = '\0';
            return 1;
        }
        set_error(result, "open file");
        return 0;
    }

    g_soundReady = 1;
    g_error[0] = '\0';

    /* .ogg/.oga cannot report their length through miniaudio (see above), so
       work it out up front while we still have the path to hand. */
    g_lengthProbe = 0.0f;
    {
        const wchar_t* dot = wcsrchr(filePath, L'.');
        if (dot != NULL && (_wcsicmp(dot, L".ogg") == 0 || _wcsicmp(dot, L".oga") == 0))
            g_lengthProbe = vorbis_length_seconds(filePath);
    }

    return 1;
}

TRPG_EXPORT void TRPG_CALL trpg_audio_close(void)
{
    close_sound();
}

/*
 * Can this file be decoded at all? Used by the library scanner to grey out
 * formats we cannot handle.
 *
 * IMPORTANT: this uses a standalone ma_decoder rather than trpg_audio_open().
 * Opening through the engine would close the track that is currently playing,
 * so merely refreshing the list would stop the music.
 */
TRPG_EXPORT int TRPG_CALL trpg_audio_probe(const wchar_t* filePath)
{
    ma_decoder decoder;
    ma_decoder_config config;
    ma_result result;

    if (filePath == NULL || filePath[0] == L'\0') return 0;

    config = ma_decoder_config_init(ma_format_f32, 0, 0);
    result = ma_decoder_init_file_w(filePath, &config, &decoder);
    if (result == MA_SUCCESS) {
        ma_decoder_uninit(&decoder);
        g_error[0] = '\0';
        return 1;
    }

    /* Not one of ours - ask Windows (m4a / aac / wma), cheaply: this only
       opens the file and negotiates a PCM output type, it does not decode. */
    if (trpg_mf_can_decode(filePath)) {
        g_error[0] = '\0';
        return 1;
    }

    set_error(result, "probe file");
    return 0;
}

TRPG_EXPORT int TRPG_CALL trpg_audio_play(void)
{
    if (!g_soundReady) { set_error_text("no track is open"); return 0; }
    if (ma_sound_start(&g_sound) != MA_SUCCESS) {
        set_error_text("cannot start playback");
        return 0;
    }
    return 1;
}

/* Pause = ma_sound_stop; the cursor stays where it is, so play resumes. */
TRPG_EXPORT int TRPG_CALL trpg_audio_pause(void)
{
    if (!g_soundReady) return 0;
    ma_sound_stop(&g_sound);
    return 1;
}

TRPG_EXPORT void TRPG_CALL trpg_audio_set_looping(int loop)
{
    if (g_soundReady) ma_sound_set_looping(&g_sound, loop ? MA_TRUE : MA_FALSE);
}

TRPG_EXPORT int TRPG_CALL trpg_audio_is_playing(void)
{
    if (!g_soundReady) return 0;
    return ma_sound_is_playing(&g_sound) == MA_TRUE ? 1 : 0;
}

TRPG_EXPORT int TRPG_CALL trpg_audio_at_end(void)
{
    if (!g_soundReady) return 1;
    /* v0.11 returns the flag directly (older releases took a ma_bool32* out). */
    return ma_sound_at_end(&g_sound) == MA_TRUE ? 1 : 0;
}

TRPG_EXPORT float TRPG_CALL trpg_audio_length(void)
{
    float seconds = 0.0f;
    if (!g_soundReady) return 0.0f;
    if (ma_sound_get_length_in_seconds(&g_sound, &seconds) == MA_SUCCESS && seconds > 0.0f)
        return seconds;
    return g_lengthProbe;   /* Ogg Vorbis: measured separately at open time */
}

TRPG_EXPORT float TRPG_CALL trpg_audio_position(void)
{
    float seconds = 0.0f;
    if (!g_soundReady) return 0.0f;
    if (ma_sound_get_cursor_in_seconds(&g_sound, &seconds) != MA_SUCCESS) return 0.0f;
    return seconds;
}

TRPG_EXPORT int TRPG_CALL trpg_audio_seek(float seconds)
{
    if (!g_soundReady) return 0;
    if (seconds < 0.0f) seconds = 0.0f;
    if (ma_sound_seek_to_second(&g_sound, seconds) != MA_SUCCESS) {
        set_error_text("seek failed");
        return 0;
    }
    return 1;
}

/* Linear volume, 0.0 - 1.0 (values above 1 amplify). */
TRPG_EXPORT void TRPG_CALL trpg_audio_set_volume(float volume)
{
    if (!g_soundReady) return;
    if (volume < 0.0f) volume = 0.0f;
    if (volume > 4.0f) volume = 4.0f;
    ma_sound_set_volume(&g_sound, volume);
}
