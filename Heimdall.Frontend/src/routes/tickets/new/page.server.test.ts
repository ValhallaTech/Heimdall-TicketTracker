// STUB: spec authored by the JS/TS Unit Test Engineer
/**
 * Stub spec for the new ticket action (`/tickets/new` +page.server.ts).
 *
 * Cases to cover (real assertions owned by the JS/TS Unit Test Engineer):
 */
import { describe, it } from 'vitest';

describe('/tickets/new +page.server.ts default action', () => {
  it.todo('redirects to /login?returnUrl=… when locals.accessToken is null');
  it.todo('POSTs PascalCase JSON to /api/v1/tickets with the bearer token');
  it.todo('sets ReporterId from locals.user.id (signed-in user as reporter)');
  it.todo('redirects (303) to /tickets/{created.Id} on 201');
  it.todo('redirects to /tickets when the 201 body is unparseable');
  it.todo('returns fail(422, { errors, values }) on a validation problem+json');
  it.todo('returns a fail with a generic error on other non-success statuses');
});
