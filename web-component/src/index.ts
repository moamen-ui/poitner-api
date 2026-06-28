// Entry point: registers the <pointer-feedback> custom element.
// Built by build.mjs into ../API/wwwroot/pointer.js as a single IIFE bundle.
// Styles live in ./styles/*.scss and build to ../API/wwwroot/pointer.css; the
// element loads that file via a <link> in its shadow root at runtime.
import { PointerFeedback } from './element';

if (!(window.customElements && window.customElements.get('pointer-feedback'))) {
  customElements.define('pointer-feedback', PointerFeedback);
}
