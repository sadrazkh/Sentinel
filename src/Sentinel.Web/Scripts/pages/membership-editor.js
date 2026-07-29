/**
 * Membership editor island on the admin user page. Mounts only when the preview target is
 * present, which it is not for a Support user viewing the page read-only.
 */
import MembershipPreview from '@/components/MembershipPreview.vue';
import { mountIsland, readJson } from '@/lib/island.js';

const form = document.getElementById('membership-form');

if (form) {
  mountIsland('#membership-preview', MembershipPreview, (element) => ({
    form,
    previewUrl: form.dataset.previewUrl,
    labels: readJson(element, 'labels', {}),
  }));
}
