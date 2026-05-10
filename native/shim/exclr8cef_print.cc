// Exclr8CEF — optional PrintToPDF extension implementation.
//
// Compiled into the same `libexclr8cef` shared library as the core shim.
// See exclr8cef_print.h for the C ABI surface and rationale.

#include "exclr8cef_print.h"

#include "include/cef_app.h"
#include "include/cef_browser.h"

#include "exclr8cef_osr.h"  // exposes exclr8cef::GetOsrBrowser

namespace {

class PdfCallback : public CefPdfPrintCallback {
public:
    PdfCallback(int browser_id, excef_pdf_done_callback_t cb)
        : browser_id_(browser_id), cb_(cb) {}

    void OnPdfPrintFinished(const CefString& /*path*/, bool ok) override {
        if (cb_) cb_(browser_id_, ok ? 1 : 0);
    }

private:
    int browser_id_;
    excef_pdf_done_callback_t cb_;
    IMPLEMENT_REFCOUNTING(PdfCallback);
};

}  // namespace

extern "C" int excef_print_to_pdf_with_settings(
        int browser_id,
        const char* path,
        const excef_pdf_settings* in,
        excef_pdf_done_callback_t callback) {
    if (!path) return 0;
    auto browser = exclr8cef::GetOsrBrowser(browser_id);
    if (!browser) return 0;

    CefPdfPrintSettings settings;
    if (in) {
        settings.landscape                 = in->landscape != 0;
        settings.print_background          = in->print_background != 0;
        settings.display_header_footer     = in->display_header_footer != 0;
        settings.prefer_css_page_size      = in->prefer_css_page_size != 0;
        settings.generate_tagged_pdf       = in->generate_tagged_pdf != 0;
        settings.generate_document_outline = in->generate_document_outline != 0;
        if (in->scale        > 0.0) settings.scale        = in->scale;
        if (in->paper_width  > 0.0) settings.paper_width  = in->paper_width;
        if (in->paper_height > 0.0) settings.paper_height = in->paper_height;
        // Margin fields: 0.0 means "use Chromium default" (~0.4in).
        settings.margin_top    = in->margin_top;
        settings.margin_bottom = in->margin_bottom;
        settings.margin_left   = in->margin_left;
        settings.margin_right  = in->margin_right;
        if (in->page_ranges && *in->page_ranges) {
            CefString(&settings.page_ranges).FromASCII(in->page_ranges);
        }
        if (in->header_template && *in->header_template) {
            CefString(&settings.header_template).FromString(in->header_template);
        }
        if (in->footer_template && *in->footer_template) {
            CefString(&settings.footer_template).FromString(in->footer_template);
        }
    }

    browser->GetHost()->PrintToPDF(path, settings,
                                   new PdfCallback(browser_id, callback));
    return 1;
}
