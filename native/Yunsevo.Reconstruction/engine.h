#pragma once
#if defined(_WIN32)
#define YV_EXPORT extern "C" __declspec(dllexport)
#else
#define YV_EXPORT extern "C" __attribute__((visibility("default")))
#endif
// Return nonzero from progress callback to cancel. All paths are UTF-8.
using YvProgress = int (*)(void*, int, const char*);
YV_EXPORT const char* yv_capabilities();
YV_EXPORT int yv_reconstruct(const char* input, const char* output, const char* backend,
    int quality, YvProgress progress, void* user);
YV_EXPORT const char* yv_last_error();
