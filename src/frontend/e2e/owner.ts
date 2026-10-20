import { join } from 'node:path';

/** The browser state of the account first-launch setup created, which logs the other tests in. */
export const ownerStorageState = join(__dirname, '../playwright/.auth/owner.json');
