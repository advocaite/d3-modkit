/* d3modapi.h - the D3ModHost client mod API (Diablo III 2.8.0.99920), for C/C++ plugins and as the reference layout for
 * bindings in other languages. Mirrors ModKit/sdk/rust/d3modapi (the canonical definition). Docs: ModKit/docs/index.html.
 *
 * A client mod is a 64-bit DLL exporting
 *     int32_t d3mod_init(const D3HostApi *api, const char *mod_id, const char *mod_dir);
 * The host calls it once on the game thread before any UI handler is resolved. Return 0 on success.
 * Check api->version >= D3MODAPI_VERSION first. Strings are UTF-8, NUL-terminated. Calling convention: Windows x64.
 *
 * Compatibility: fields are only ever APPENDED (with D3MODAPI_VERSION + 1); nothing is removed or reordered.
 */
#ifndef D3MODAPI_H
#define D3MODAPI_H

#include <stddef.h>
#include <stdint.h>

#define D3MODAPI_VERSION 7

#ifdef __cplusplus
extern "C" {
#endif

/* A game UI control (opaque). */
typedef void *D3Control;
/* A UI handler: the game's rcx, rdx, r8, r9. For click handlers rcx = the clicked control's handle (see is_control). */
typedef uintptr_t (*D3Handler)(uintptr_t rcx, uintptr_t rdx, uintptr_t r8, uintptr_t r9);
/* A wrapper around a handler the exe has: (original, rcx, rdx, r8, r9); call original to keep the game's behaviour. */
typedef uintptr_t (*D3Wrapper)(D3Handler original, uintptr_t rcx, uintptr_t rdx, uintptr_t r8, uintptr_t r9);
/* A draw hook (layout style 44): (flag, depth). Draw at z = 0.9999 - *depth * 1e-4 and increase *depth. */
typedef uintptr_t (*D3DrawFn)(uintptr_t flag, float *depth);
typedef uintptr_t (*D3DrawWrapper)(D3DrawFn original, uintptr_t flag, float *depth);

typedef struct D3HostApi {
    uint32_t version;
    /* --- version 1: handlers and controls */
    void      (*log)(const char *line);
    int32_t   (*register_handler)(const char *name, D3Handler f);
    int32_t   (*register_wrapper)(const char *name, D3Wrapper w);
    D3Control (*find_control)(const char *full_path);
    D3Control (*child_of)(D3Control control, const char *relative_path);
    void      (*set_visible)(D3Control control, int32_t visible);
    void      (*set_text)(D3Control control, const char *text);
    intptr_t  (*get_text)(D3Control control, uint8_t *buf, size_t len);
    void      (*pin_sno)(int32_t sno);
    D3Control (*create_layout)(int32_t sno);
    int32_t   (*list_add_item)(D3Control list);
    D3Control (*list_get_item)(D3Control list, int32_t index);
    int32_t   (*is_control)(uintptr_t handler_rcx, D3Control control);
    intptr_t  (*login_token)(uint8_t *buf, size_t len);
    void      (*dump_controls)(const char *needle);
    /* --- version 2: drawing (rectangles are {left, top, right, bottom} in UI units, 1600 x 1200) */
    int32_t   (*register_draw)(const char *stock_draw_hook, D3DrawWrapper w);
    int32_t   (*control_rect)(D3Control control, float out_rect[4]);
    void      (*mouse_pos)(float out_pos[2]);
    void      (*draw_item_icon)(int32_t gbid, const float rect[4], float *depth);
    int32_t   (*draw_texture)(const char *name, const float rect[4], uint32_t argb, float *depth);
    /* --- version 4: item tooltips (generator = serialized D3.Items.Generator) */
    int32_t   (*show_item_tooltip)(uintptr_t hover, const uint8_t *generator, size_t len, int32_t gbid, int32_t class_index,
                                   int32_t male, const float anchor[4]);
    /* --- version 5 */
    intptr_t  (*item_name)(const uint8_t *generator, size_t len, int32_t class_index, int32_t male, uint8_t *buf, size_t buf_len);
    /* --- version 6 */
    intptr_t  (*item_line)(const uint8_t *generator, size_t len, int32_t class_index, int32_t male, int32_t index,
                           uint8_t *buf, size_t buf_len);
    /* --- version 7: dropdowns (UIComboBox, layout control type 20) */
    int32_t   (*combo_set_items)(D3Control combo, const char *const *texts, const uint64_t *data, size_t count);
    int32_t   (*combo_select)(D3Control combo, int32_t index, int32_t fire);
    int32_t   (*combo_selected)(D3Control combo, int32_t *index, uint64_t *data);
    int32_t   (*combo_set_placeholder)(D3Control combo, const char *text);
} D3HostApi;

/* The stock draw-hook names a mod layout's style-44 can use (2.8 resolves only these). */
static const char *const D3_DRAW_HOOK_NAMES[9] = {
    "UIShop_Draw", "UIInventory_Draw", "UIInspect_Draw", "UIEquipmentManager_DrawInventory", "PlayerInspect_InventoryDraw",
    "UITransmute_DrawGrid", "UIHireling_Draw", "UIGameBase_Draw", "UIStash_Draw",
};

#ifdef __cplusplus
}
#endif
#endif
