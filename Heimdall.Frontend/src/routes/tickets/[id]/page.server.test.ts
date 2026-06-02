// STUB: spec authored by the JS/TS Unit Test Engineer
/**
 * Stub spec for the ticket edit loader + action
 * (`/tickets/[id]` +page.server.ts).
 *
 * Cases to cover (real assertions owned by the JS/TS Unit Test Engineer):
 */
import { describe, it } from 'vitest';

describe('/tickets/[id] +page.server.ts load', () => {
  it.todo('redirects to /login?returnUrl=… when locals.accessToken is null');
  it.todo('GETs /api/v1/tickets/{id} with the bearer token');
  it.todo('throws error(404) when the API responds 404');
  it.todo('throws error(403) when the API responds 403');
  it.todo('throws error(404) when the body cannot be parsed as a ticket');
  it.todo('returns { ticket } on success');
});

describe('/tickets/[id] +page.server.ts default action', () => {
  it.todo('redirects to /login?returnUrl=… when locals.accessToken is null');
  it.todo('PUTs PascalCase JSON to /api/v1/tickets/{id} with Id forced to the route id');
  it.todo('maps an empty AssigneeId to null');
  it.todo('redirects (303) to /tickets/{id} on 204');
  it.todo('throws error(404) when the API responds 404');
  it.todo('returns fail(422, { errors, values }) on a validation problem+json');
  it.todo('returns a fail with a generic error on other non-success statuses');
});
