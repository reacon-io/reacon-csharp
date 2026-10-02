# Created with Openapi Generator
See the project's [REAMDE](src/Reacon.Sdk/README.md)
The service URL is fixed to https://api.reacon.io. SDKs do not accept a service URL override.

## Retrying requests

A timeout or dropped connection does not prove that the server rejected a request. Before retrying a write or credit-consuming operation, check its outcome. Only retry when the operation is safe to repeat; when present, respect the Retry-After response header. Keep application-level retries bounded as well: wrapping an SDK call in an unbounded retry loop can multiply requests and repeat side effects.
