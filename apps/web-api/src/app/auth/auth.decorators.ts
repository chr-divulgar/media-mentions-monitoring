import { SetMetadata } from '@nestjs/common';

export const IS_PUBLIC_KEY = 'isPublic';
export const ROLES_KEY = 'roles';

/** Opts a route out of the global AuthGuard (e.g. /auth/profile, which verifies the token itself). */
export const Public = () => SetMetadata(IS_PUBLIC_KEY, true);

/** Restricts a route to these roles; without it any approved (non-'initial') user is enough. */
export const Roles = (...roles: string[]) => SetMetadata(ROLES_KEY, roles);
