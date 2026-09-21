# Visual Direction: "The Living Spec"

**Thesis:** *An AI coding tool is only as good as the feedback it receives.* 
Pointer bridges human thought and machine execution. Rather than mimicking a PR review, this design feels like a structural, precision-engineered canvas—a living blueprint where raw feedback snaps into actionable data.

**Color Palette:**
- **Brand Accent (Premium Model):** `#FF4F00` (International Orange) – energetic, focused, signifies the orchestrator's high-level judgment.
- **Worker Tier (Cheaper Model):** `#00E5FF` (Cyan) – cool, technical, distinct from state colors (not red/error, not green/done).
- **Background:** `#FAFAFA` (Light) / `#0A0A0A` (Dark)
- **Surfaces:** `#FFFFFF` (Light) / `#141414` (Dark)
- **Text:** `#111111` & `#666666` (Light) / `#EDEDED` & `#999999` (Dark)
- **Borders/Grid:** `#EAEAEA` (Light) / `#222222` (Dark)

**Typography:**
- **Headings & UI:** *Inter* – sharp, clean, unopinionated geometry.
- **Code, Data, Labels:** *JetBrains Mono* – distinctly developer-focused, used for DOM selectors and metrics.
- **Arabic:** *Cairo* – pairs cleanly with Inter for RTL support.

**Shape & Motion Language:**
- **Shape:** Precision over softness. Sharp corners (0px) or microscopic 2px radii. Distinct structural borders (1px solid) separating layout regions to evoke a wireframe or schematic diagram.
- **Motion:** Snappy, rigid, step-driven. 150ms linear/ease-out transitions. No bouncy springs.

**Hero Composition (Viewport 1):**
- **Left/Top:** Massive, tight-leading typography delivering the promise. Two CTAs (Primary solid Orange, Secondary outline).
- **Right/Bottom:** A CSS-animated structural visualization. A human-language comment bubble ("Make this button larger") slides into a rigid "Parser" bracket, outputting a structured, monospace DOM payload (`button#submit`, `width: 100%`) that wires into a Git Commit node. It instantly proves the mechanism of "comment becomes change."

**Cost-Aware Apply Graph & Translation:**
- **Graph:** A minimalist, hard-edged SVG bar chart. Bar A is a monolithic `#FF4F00` block. Bar B is segmented: a top `#FF4F00` block over a visibly smaller `#00E5FF` block, emphasizing the delegation of mechanical work to the cheaper tier.
- **Translation:** Rendered as a distinct "filter node" in the visual flow, utilizing the `#00E5FF` (worker model) color to highlight that translation happens in the background, transforming a non-English string into structured English payload.
