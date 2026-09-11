<script lang="ts">
  import { auth, errorMessage } from '$lib/api/client';
  import { PageHeader, Card, Input, Button, Alert, toast } from '$lib/components/ui';

  let currentPassword = $state('');
  let newPassword = $state('');
  let confirmPassword = $state('');
  let saving = $state(false);
  let error = $state('');

  async function handleSubmit() {
    error = '';

    if (!currentPassword || !newPassword || !confirmPassword) {
      error = 'All fields are required.';
      return;
    }

    if (newPassword !== confirmPassword) {
      error = 'New password and confirmation do not match.';
      return;
    }

    if (newPassword.length < 8) {
      error = 'New password must be at least 8 characters long.';
      return;
    }

    saving = true;
    try {
      await auth.changePassword(currentPassword, newPassword);
      toast.success('Password changed successfully');
      currentPassword = '';
      newPassword = '';
      confirmPassword = '';
    } catch (e: unknown) {
      error = errorMessage(e);
    } finally {
      saving = false;
    }
  }
</script>

<svelte:head><title>Change password · FlowWeaver</title></svelte:head>

<div class="p-6 max-w-xl mx-auto space-y-6">
  <PageHeader
    title="Change password"
    description="Update your account password"
    breadcrumbs={[{ label: 'Settings' }, { label: 'Password' }]}
  />

  <Card>
    <form onsubmit={handleSubmit} class="space-y-4">
      {#if error}
        <Alert tone="error" title="Error" dismissible onDismiss={() => (error = '')}>{error}</Alert>
      {/if}

      <Input
        type="password"
        label="Current password"
      help="account.current_password"
        bind:value={currentPassword}
        autocomplete="current-password"
        required
      />

      <Input
        type="password"
        label="New password"
      help="account.new_password"
        bind:value={newPassword}
        autocomplete="new-password"
        required
      />

      <Input
        type="password"
        label="Confirm new password"
      help="account.confirm_password"
        bind:value={confirmPassword}
        autocomplete="new-password"
        required
      />

      <div class="flex justify-end pt-2">
        <Button type="submit" variant="primary" loading={saving}>Change password</Button>
      </div>
    </form>
  </Card>
</div>
