<script lang="ts">
  import DocLayout from '../_components/DocLayout.svelte';
  import Callout from '../_components/Callout.svelte';
</script>

<DocLayout
  title="Themes"
  lead="FlowWeaver ships seven built-in themes, and lets you build your own from a handful of base colours plus a few style knobs — corners, fonts, heading weight, interface scale. A theme you save appears in the palette picker beside the built-in ones; an admin can publish one to everybody."
>
  <Callout tone="where" title="Where to find it">
    Studio: <a href="/themes"><code>/themes</code></a> ·
    Picker: the palette icon at the bottom of the sidebar ·
    Also linked from <a href="/admin"><code>/admin</code></a> → Tools.
  </Callout>

  <section>
    <h2>Concept</h2>
    <p>
      A theme is <strong>seven base colours</strong> — one per palette — not a
      stylesheet. Each base expands into the 11 shades
      (<code>50</code>…<code>950</code>) the interface actually uses, plus the
      contrast tokens that decide whether text on a filled button comes out
      dark or light. That expansion happens in your browser every time the app
      loads, which is why the editor is a row of colour pickers rather than a
      77-field form.
    </p>
    <dl>
      <dt>Primary</dt><dd>Buttons, links, active navigation, focus rings.</dd>
      <dt>Secondary</dt><dd>Secondary actions and accents.</dd>
      <dt>Tertiary</dt><dd>Charts and informational highlights.</dd>
      <dt>Success / Warning / Error</dt><dd>Run states, queue health, destructive actions.</dd>
      <dt>Surface</dt><dd>Page background, cards, borders and body text — the one that changes the most.</dd>
    </dl>
    <p>
      Any palette you leave at its default keeps the FlowWeaver brand colour, so
      "the standard look but our brand blue" is a one-colour theme.
    </p>
    <p>
      You don't have to start from the brand palette: the
      <strong>Start from</strong> row loads a curated preset (Midnight, Ocean,
      Forest, …) into the editor, and <strong>Surprise me</strong> rolls a
      random palette that keeps green/amber/red where status colours belong.
      Both only fill the form — nothing is saved until you save.
    </p>
  </section>

  <section>
    <h2>Beyond colour: the Style section</h2>
    <p>
      A theme can also carry a handful of non-colour settings. Anything you
      leave at its default simply inherits the standard FlowWeaver look, so an
      existing colours-only theme is unaffected.
    </p>
    <dl>
      <dt>Corner roundness</dt>
      <dd>
        One multiplier over every radius in the interface — <code>0</code> is
        fully square, <code>2</code> is very round. It scales buttons, cards,
        inputs and badges together so the result stays coherent.
      </dd>
      <dt>Interface scale</dt>
      <dd>
        Grows or shrinks the whole app — text and spacing alike — between 85%
        and 115%. Think of it as a persistent browser zoom that follows the
        theme: handy for a NOC wall display or a dense laptop setup.
      </dd>
      <dt>Body, heading and code fonts</dt>
      <dd>
        Chosen from a fixed set of stacks: the two fonts the app bundles
        (Inter, JetBrains Mono) plus system-safe families. The set is closed on
        purpose — a shared theme must never be able to pull a font from a
        remote server into everyone's browser.
      </dd>
      <dt>Heading weight</dt>
      <dd>
        How heavy titles render, 400–900. The default is 650; the Brutalist
        built-in runs at 800 if you want a reference point.
      </dd>
    </dl>
  </section>

  <section>
    <h2>Contrast warnings</h2>
    <p>
      The editor checks each palette against the combination it actually
      powers: accents are measured as label text on a filled
      <code>500</code> button (WCAG AA wants 4.5:1), surface as body text
      between the two ramp ends. A palette that lands below the threshold gets
      a warning under its picker — a warning, not a blocker, because a
      deliberately soft decorative theme is allowed to exist. Themes meant for
      real daily work should clear it.
    </p>
  </section>

  <section>
    <h2>Duplicating, importing, exporting</h2>
    <ul>
      <li>
        <strong>Duplicate</strong> — every saved theme (yours or a shared one)
        has a duplicate action that loads a copy into the editor; saving
        creates your own private theme. This is also how you riff on a shared
        theme you can't edit.
      </li>
      <li>
        <strong>Export JSON</strong> — copies the theme (name, colours,
        settings) to the clipboard as JSON, ready to paste into a chat, a
        ticket or another FlowWeaver instance.
      </li>
      <li>
        <strong>Import</strong> — pastes that JSON back in. It's validated
        (palette names, hex values, setting ranges) and loads as a
        <em>new</em> unsaved theme, so an import can never silently overwrite
        something you already saved.
      </li>
    </ul>
  </section>

  <section>
    <h2>How a base colour becomes a ramp</h2>
    <p>
      Shades are interpolated in <strong>OKLCh</strong>, not HSL: hue and
      perceived saturation stay put while only lightness moves, which is what
      keeps a generated ramp from going muddy in the mid-tones. Two behaviours
      are worth knowing about:
    </p>
    <ul>
      <li>
        <strong>Your colour survives verbatim</strong> — it appears in the ramp
        exactly as picked, never re-quantised.
      </li>
      <li>
        <strong>It lands in the shade its lightness belongs to</strong>, which
        is not always <code>500</code>. Pick a near-black surface and it becomes
        shade <code>950</code>; pick a pale cream and it becomes <code>50</code>.
        The strip under each picker marks the slot it occupied. Forcing every
        base into the 500 slot would put a near-black between two much lighter
        shades and the ramp would stop being a ramp.
      </li>
    </ul>
    <Callout tone="info" title="Where the brand colour ends up">
      If you want a specific colour on the <em>buttons</em>, watch the marker on
      the Primary strip: buttons use shade 500. A base that anchors at 600 will
      render buttons one step lighter than the swatch you picked — nudge it
      lighter until the marker reads 500.
    </Callout>
    <p>
      Saturated hues can't hold their chroma at the ends of a ramp, so chroma is
      tapered towards both extremes and then reduced until the colour fits in
      sRGB. Clipping the channels instead would drift the hue — a bright red
      would turn orange at the light end.
    </p>
  </section>

  <section>
    <h2>Light and dark</h2>
    <p>
      You don't define two themes. A ramp is shared between modes and the
      interface picks opposite ends of it per mode — a card that is
      <code>surface-100</code> in light mode is <code>surface-900</code> in
      dark. That's exactly how the built-in themes work, so your theme keeps
      responding to the light/dark toggle. The preview has its own mode switch
      so you can check both without flipping the whole app.
    </p>
  </section>

  <section>
    <h2>Private vs shared</h2>
    <table>
      <thead><tr><th></th><th>Who sees it</th><th>Who can change it</th></tr></thead>
      <tbody>
        <tr>
          <td><strong>Private</strong></td>
          <td>Only you. It isn't listed or fetchable by anyone else.</td>
          <td>You.</td>
        </tr>
        <tr>
          <td><strong>Shared</strong></td>
          <td>Every user, in their picker.</td>
          <td>Any admin. The author too, if they're not an admin any more.</td>
        </tr>
      </tbody>
    </table>
    <p>
      Publishing — and unpublishing — is admin-only in both directions.
      Unpublishing takes the theme out of the picker for everyone who selected
      it, and they fall back to Editorial. Everyone can create private themes;
      no special permission is needed.
    </p>
    <p>
      Names are unique within a scope, not globally: two people can each keep a
      private "Midnight", but only one shared theme can carry that name.
    </p>
  </section>

  <section>
    <h2>Selecting and persistence</h2>
    <p>
      Your choice lives in the browser (<code>localStorage</code>), like the
      light/dark mode — it follows the browser, not the account. The generated
      CSS is cached alongside it so a custom theme paints before the page
      hydrates instead of flashing the default palette on every load.
    </p>
    <p>
      If a theme you're using gets deleted or unpublished, the app notices on
      the next load and falls back to Editorial rather than leaving you on a
      theme nobody defines any more.
    </p>
  </section>

  <section>
    <h2>Deleting</h2>
    <p>
      Deleting is a soft delete: the row survives so anyone still holding that
      theme selected can be recovered, but the name is immediately free to reuse.
      Deleting a shared theme is recorded in the <a href="/admin/audit">audit
      log</a>, along with publishing and unpublishing — private themes aren't
      logged, since they're a personal preference.
    </p>
  </section>
</DocLayout>
