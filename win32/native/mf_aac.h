/*
 * C interface to the Media Foundation decoder in mf_aac.cpp.
 *
 * Kept C-only so audio_bridge.c does not need to know about C++ or COM:
 * it calls decode/probe and gets back plain memory.
 */
#ifndef TRPG_MF_AAC_H
#define TRPG_MF_AAC_H

#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Decode a whole file to interleaved 16-bit PCM (asked from Media Foundation,
 * which builds the AAC/MP3/WMA decoder chain itself).
 *
 * On success *ppPcm owns a malloc'd block that the caller must release with
 * trpg_mf_free(). Returns 1 on success, 0 on failure (err receives a message).
 */
int trpg_mf_decode_file(const wchar_t* path, void** ppPcm, long long* pFrames,
                        int* pChannels, int* pSampleRate, char* err, int errCap);

void trpg_mf_free(void* p);

/* Can this file be opened and negotiated to PCM? No decoding. */
int trpg_mf_can_decode(const wchar_t* path);

const char* trpg_mf_last_error(void);

#ifdef __cplusplus
}
#endif

#endif /* TRPG_MF_AAC_H */
