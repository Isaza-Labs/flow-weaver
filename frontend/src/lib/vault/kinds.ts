// The kinds of entry the Vault holds, and how each maps onto the two stores behind
// it. Five kinds are device/integration credentials; the sixth is a secret value
// (one named value an OpenAPI spec points at with a ${secret:…} reference).
//
// The stores and their APIs are unchanged. A credential has only two auth methods
// (password or SSH key); what it is *for* lives in its `type` (ssh, netconf,
// snmp_v3, git_token, api_key, http_basic, or anything custom), and that is what
// the pickers on devices, Git remotes and integrations filter on. So a kind here is
// read from the auth method first and the type second, and the type stays visible
// and editable on the form as "Used for".

import type { Credential } from '$lib/api/client';
import type { IconComponent } from '$lib/components/ui';
import { FileKey, GitBranch, KeyRound, Lock, RadioTower, UserRound } from 'lucide-svelte';

export type VaultKind = 'login' | 'ssh_key' | 'snmp' | 'git_token' | 'api_key' | 'secret';

export interface VaultKindInfo {
	kind: VaultKind;
	/** As it appears on a badge and in the type picker. */
	label: string;
	/** What it is for, in one line — shown in the type picker. */
	desc: string;
	icon: IconComponent;
	/** The credential auth method behind it; absent for a secret value. */
	authMethod?: 'password' | 'key';
	/**
	 * The credential types this kind is used for, first one the default. A new
	 * entry starts with the default, shown in the form so the admin sees what will
	 * be stored; "custom" is always offered on top.
	 */
	types?: string[];
}

export const VAULT_KINDS: VaultKindInfo[] = [
	{
		kind: 'login',
		label: 'Login',
		desc: 'A username and password — SSH and NETCONF devices, HTTP Basic APIs.',
		icon: UserRound,
		authMethod: 'password',
		types: ['ssh', 'netconf', 'http_basic']
	},
	{
		kind: 'ssh_key',
		label: 'SSH key',
		desc: 'A username with a private key, and its passphrase if it has one.',
		icon: FileKey,
		authMethod: 'key',
		types: ['ssh', 'netconf']
	},
	{
		kind: 'snmp',
		label: 'SNMPv3 user',
		desc: 'An SNMPv3 user and its passphrase, for the SNMP handler.',
		icon: RadioTower,
		authMethod: 'password',
		types: ['snmp_v3']
	},
	{
		kind: 'git_token',
		label: 'Git token',
		desc: 'A personal access token for HTTPS Git remotes, with the account name.',
		icon: GitBranch,
		authMethod: 'password',
		types: ['git_token']
	},
	{
		kind: 'api_key',
		label: 'API key',
		desc: 'A bearer or header token for REST integrations.',
		icon: KeyRound,
		authMethod: 'password',
		types: ['api_key']
	},
	{
		kind: 'secret',
		label: 'Secret value',
		desc: 'One named value an OpenAPI spec points at with a ${secret:…} reference.',
		icon: Lock
	}
];

const BY_KIND = new Map(VAULT_KINDS.map((k) => [k.kind, k]));

export function kindInfo(kind: VaultKind): VaultKindInfo {
	return BY_KIND.get(kind)!;
}

/** The type a new credential of this kind starts with; '' for a secret value. */
export function defaultTypeOf(kind: VaultKind): string {
	return kindInfo(kind).types?.[0] ?? '';
}

/**
 * The kind's label as it reads mid-sentence ("New login", "New API key"): only a
 * leading capital that starts an ordinary word is lowered, so the acronyms SSH,
 * SNMPv3 and API keep their capitals.
 */
export function kindNoun(kind: VaultKind): string {
	const label = kindInfo(kind).label;
	return /^[A-Z][a-z]/.test(label) ? label[0].toLowerCase() + label.slice(1) : label;
}

/**
 * The Vault kind of a stored credential. Method and type are trimmed and
 * lowercased first: rows written through older paths hold "KEY" or " Git_Token ".
 * A key is an SSH key whatever it is used for; a password-type credential is read
 * from its type, and anything else (ssh, netconf, http_basic, custom) is a login.
 */
export function kindOfCredential(c: Pick<Credential, 'auth_method' | 'type'>): VaultKind {
	const method = String(c.auth_method ?? '').trim().toLowerCase();
	if (method === 'key') return 'ssh_key';
	switch (String(c.type ?? '').trim().toLowerCase()) {
		case 'snmp_v3':
			return 'snmp';
		case 'git_token':
			return 'git_token';
		case 'api_key':
			return 'api_key';
		default:
			return 'login';
	}
}

export function isVaultKind(value: string | null | undefined): value is VaultKind {
	return !!value && BY_KIND.has(value as VaultKind);
}
