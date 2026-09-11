<script lang="ts">
    import { goto } from "$app/navigation";
    import { page } from "$app/state";
    import { onMount, tick } from "svelte";
    import { auth, errorMessage } from "$lib/api/client";
    import { authStore } from "$lib/stores/auth.svelte";
    import { Button, Input, Alert, toast } from "$lib/components/ui";
    import {
        LogIn,
        User as UserIcon,
        Lock,
        ShieldCheck,
    } from "lucide-svelte";
    import { APP_VERSION } from "$lib/utils/version";

    let username = $state("");
    let password = $state("");
    let loading = $state(false);
    let formError = $state<string | null>(null);

    // Explicit IDs so we can resolve the inputs via getElementById on
    // mount. The Input component doesn't expose a bind:el binding, so
    // this is the cleanest way to drop focus on the right field.
    const USERNAME_ID = "login-username";
    const PASSWORD_ID = "login-password";

    const COPY_YEAR = 2026;

    onMount(async () => {
        if (authStore.isAuthenticated) {
            const redirect = page.url.searchParams.get("redirect") ?? "/";
            goto(redirect);
            return;
        }
        // Wait for hydration, then drop focus on the first field so
        // keyboard users land where they need to type.
        await tick();
        document.getElementById(USERNAME_ID)?.focus({ preventScroll: true });
    });

    async function onSubmit(event: SubmitEvent) {
        event.preventDefault();
        if (loading) return;

        formError = null;
        if (!username.trim() || !password) {
            formError = "Username and password are required.";
            // Move focus to the first invalid field — WCAG focus-management.
            await tick();
            const invalidId = !username.trim() ? USERNAME_ID : PASSWORD_ID;
            document.getElementById(invalidId)?.focus({ preventScroll: true });
            return;
        }

        loading = true;
        try {
            // No workspace slug is sent: the backend resolves the account
            // from the username alone, and refuses the login outright if that
            // username is not unique. See the comment on the field removal.
            await auth.login(username.trim(), password);
            toast.success("Signed in");
            const redirect = page.url.searchParams.get("redirect") ?? "/";
            goto(redirect);
        } catch (err) {
            formError = errorMessage(err);
        } finally {
            loading = false;
        }
    }
</script>

<svelte:head>
    <title>Sign in · FlowWeaver</title>
    <meta
        name="description"
        content="Sign in to your FlowWeaver workspace — automate every network workflow, woven into one platform."
    />
</svelte:head>

<!-- ╭──────────────────────────────────────────────────────────╮
     │ Split-panel login.                                       │
     │ - Left pane (5fr): brand hero with the FlowWeaver        │
     │   identity. Indigo gradient backdrop with subtle yellow  │
     │   + coral atmospheric washes, faint geometric grid       │
     │   overlay for texture (a quiet nod to "network" without  │
     │   becoming literal), editorial micro-label above the     │
     │   logo, hero copy with a brand-yellow accent underline,  │
     │   and a stats row (workflows / devices / uptime) doing   │
     │   double duty as social proof.                           │
     │ - Right pane (4fr): form on neutral surface so the input │
     │   fields, not the chrome, command attention. Editorial   │
     │   micro-label above the h2 to mirror the brand pane.     │
     │                                                          │
     │ Layout collapses to a single column below md. The brand  │
     │ pane stacks above, the form below it. min-h-dvh uses the │
     │ dynamic viewport so mobile browser chrome doesn't clip.  │
     ╰──────────────────────────────────────────────────────────╯ -->
<div
    class="min-h-dvh grid md:grid-cols-[7fr_5fr] bg-surface-50-950 overflow-x-hidden"
>
    <!-- ─── BRAND PANE ──────────────────────────────────────── -->
    <section
        aria-labelledby="brand-heading"
        class="relative flex flex-col justify-between p-8 md:p-12 lg:p-14 overflow-hidden text-white isolate"
        style="background: linear-gradient(135deg, var(--color-primary-500) 0%, var(--color-primary-800) 100%);"
    >
        <!-- Atmospheric brand-color washes. Low opacity, brand-only colours.
         aria-hidden because they're pure decoration. -->
        <div
            class="absolute inset-0 pointer-events-none -z-10"
            aria-hidden="true"
            style="background:
        radial-gradient(ellipse 70% 55% at 8% 8%, color-mix(in oklab, var(--color-warning-500) 32%, transparent), transparent 60%),
        radial-gradient(ellipse 60% 55% at 92% 92%, color-mix(in oklab, var(--color-secondary-500) 30%, transparent), transparent 60%);"
        ></div>

        <!-- Geometric grid texture — quiet "network" reference without
         a literal network diagram. Stroke-only SVG pattern, very low
         opacity so it reads as paper texture rather than ornament. -->
        <svg
            class="absolute inset-0 w-full h-full pointer-events-none -z-10 opacity-[0.06]"
            aria-hidden="true"
        >
            <defs>
                <pattern
                    id="weave-grid"
                    width="56"
                    height="56"
                    patternUnits="userSpaceOnUse"
                >
                    <path
                        d="M 56 0 L 0 0 0 56"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="0.6"
                    />
                </pattern>
            </defs>
            <rect width="100%" height="100%" fill="url(#weave-grid)" />
        </svg>

        <!-- Hairline corner ornaments — minimalist editorial accent.
         Top-left + bottom-right brackets frame the content. -->
        <span
            class="absolute top-6 left-6 w-10 h-10 border-t-2 border-l-2 border-white/25 pointer-events-none -z-10"
            aria-hidden="true"
        ></span>
        <span
            class="absolute bottom-6 right-6 w-10 h-10 border-b-2 border-r-2 border-white/25 pointer-events-none -z-10"
            aria-hidden="true"
        ></span>

        <!-- ── TOP: editorial micro-label + brand logo ── -->
        <header
            class="relative z-10 flex flex-col gap-7 animate-in fade-in slide-up"
        >
            <div
                class="font-mono text-[11px] tracking-[0.24em] uppercase text-white/55 inline-flex items-center gap-3"
            >
                <span
                    class="inline-block w-8 border-t border-white/40"
                    aria-hidden="true"
                ></span>
                FlowWeaver · Sign in
            </div>
            <!-- Logo container — fixed height (h-44 / 176 px), transform scale
           crops the PNG whitespace. Drop-shadow lifts the mark off the
           gradient so it reads as foreground, not flat sticker. -->
            <div
                class="w-full max-w-lg h-44 overflow-hidden flex items-center"
                aria-hidden="true"
            >
                <img
                    src="/FlowWeaver_Logo_1.png"
                    alt=""
                    class="h-full w-auto"
                    style="transform: scale(2); transform-origin: left center; filter: drop-shadow(0 10px 24px rgba(0,0,0,0.34)) brightness(1.05);"
                />
            </div>
        </header>

        <!-- ── MIDDLE: hero copy + stats ── -->
        <div
            class="relative z-10 max-w-xl animate-in fade-in slide-up"
            style="animation-delay: 80ms; animation-fill-mode: backwards;"
        >
            <h1
                id="brand-heading"
                class="text-3xl md:text-4xl lg:text-[2.875rem] font-bold tracking-[-0.028em] leading-[1.04] mb-5"
            >
                Automate every
                <span class="relative whitespace-nowrap">
                    network workflow
                    <!-- Brand-yellow marker underline. Sits behind the text via
               z-index, drawn as a thick rounded bar so it reads as
               highlighter, not link. Theme-token so it follows the
               active palette. -->
                    <span
                        class="absolute left-0 right-0 bottom-[0.06em] h-[0.22em] rounded-full -z-10 opacity-90"
                        style="background: var(--color-warning-500);"
                        aria-hidden="true"
                    ></span>
                </span>.
                <span class="block mt-2 text-white/85 font-semibold">
                    Woven into one platform.
                </span>
            </h1>
            <p
                class="text-base md:text-lg text-white/72 leading-relaxed max-w-md"
            >
                Schedule, execute, and observe workflows across thousands of
                devices — from a single pane.
            </p>
        </div>

        <!-- ── BOTTOM: security + version ── -->
        <footer
            class="relative z-10 flex items-center justify-between gap-4 text-[11px] text-white/55 font-mono tracking-[0.18em] uppercase pt-5 border-t border-white/12 animate-in fade-in slide-up"
            style="animation-delay: 160ms; animation-fill-mode: backwards;"
        >
            <div class="inline-flex items-center gap-2.5 min-w-0">
                <ShieldCheck
                    size={14}
                    class="text-warning-300 shrink-0"
                    aria-hidden="true"
                />
                <span class="truncate">SOC 2 · ISO 27001 · TLS 1.3</span>
            </div>
            <span class="tabular-nums shrink-0">v{APP_VERSION}</span>
        </footer>
    </section>

    <!-- ─── FORM PANE ──────────────────────────────────────── -->
    <section
        aria-labelledby="form-heading"
        class="relative flex items-center justify-center p-6 md:p-10 lg:p-14"
    >
        <div
            class="w-full max-w-sm animate-in fade-in slide-up"
            style="animation-delay: 120ms; animation-fill-mode: backwards;"
        >
            <!-- Editorial micro-label mirrors the brand pane for visual unity. -->
            <header class="mb-8">
                <p
                    class="font-mono text-[11px] tracking-[0.24em] uppercase text-surface-500 mb-3 inline-flex items-center gap-2.5"
                >
                    <span
                        class="inline-block w-6 border-t border-surface-400"
                        aria-hidden="true"
                    ></span>
                    Welcome back
                </p>
                <h2
                    id="form-heading"
                    class="text-2xl md:text-[28px] font-bold tracking-[-0.024em] text-surface-900-100 mb-2 leading-tight"
                >
                    Sign in to your workspace
                </h2>
                <p class="text-sm text-surface-600-400 leading-relaxed">
                    Enter your credentials to access FlowWeaver.
                </p>
            </header>

            <!-- Polite live region — announces async state ("Signing in…") to
           screen readers without interrupting them. Empty by default. -->
            <div aria-live="polite" class="sr-only">
                {loading ? "Signing in, please wait" : ""}
            </div>

            {#if formError}
                <!-- role="alert" forces immediate screen-reader announcement
             when the error first appears. Skeleton's Alert already
             renders the visual; we add the role for assertive a11y. -->
                <div class="mb-4" role="alert">
                    <Alert tone="error">{formError}</Alert>
                </div>
            {/if}

            <form onsubmit={onSubmit} class="flex flex-col gap-4" novalidate>
                <!-- A <fieldset disabled> propagates disabled state to every
             native input inside automatically — cleaner than passing
             `disabled={loading}` to each <Input>. display:contents
             keeps the fieldset out of the layout flow so the gap-4
             on the parent form continues to work. -->
                <fieldset disabled={loading} class="contents">
                    <legend class="sr-only">Sign in credentials</legend>
                    <Input
                        id={USERNAME_ID}
                        label="Username"
                        help="login.username"
                        type="text"
                        autocomplete="username"
                        icon={UserIcon}
                        bind:value={username}
                        placeholder="admin"
                    />
                    <Input
                        id={PASSWORD_ID}
                        label="Password"
                        help="login.password"
                        type="password"
                        autocomplete="current-password"
                        icon={Lock}
                        bind:value={password}
                        placeholder="••••••••"
                    />
                </fieldset>

                <div class="mt-3">
                    <Button
                        type="submit"
                        variant="primary"
                        fullWidth
                        {loading}
                        icon={LogIn}
                    >
                        {loading ? "Signing in…" : "Sign in"}
                    </Button>
                </div>
            </form>

            <!-- Helper / escape hatch. Real <a> so keyboard nav reaches it
           and the contact mailto: actually works. Anchor colour falls
           back to primary in light and warning (brand yellow) in dark
           — the brand's hero accent reaches into the form pane in
           dark mode without dominating in light. -->
            <div class="mt-8 pt-6 border-t border-surface-200-800/60">
                <p class="text-xs text-center text-surface-500">
                    Trouble signing in?
                    <a
                        href="mailto:support@flowweaver.com"
                        class="font-medium text-primary-600 dark:text-warning-400 hover:underline underline-offset-2"
                    >
                        Contact your administrator
                    </a>
                </p>
                <p
                    class="mt-3 text-[10px] text-center text-surface-400-600 font-mono tracking-[0.18em] uppercase tabular-nums"
                >
                    © {COPY_YEAR} FlowWeaver · v{APP_VERSION}
                </p>
            </div>
        </div>
    </section>
</div>
