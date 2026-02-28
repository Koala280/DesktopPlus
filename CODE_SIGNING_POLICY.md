# Code signing policy

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

## Project scope

This policy applies to DesktopPlus and all binaries produced from this repository:

- Repository: `https://github.com/Koala280/DesktopPlus`
- Project owner and maintainer: [Koala280](https://github.com/Koala280)

## Team roles

DesktopPlus is maintained by one person. Roles are assigned as follows:

- Committer and reviewer: [Koala280](https://github.com/Koala280)
- Approver: [Koala280](https://github.com/Koala280)

## What can be signed

- Only binaries built from this repository are signed.
- Only release artifacts produced by the documented build/release scripts are signed.
- Upstream third-party binaries are not re-signed under this project identity.

## Privacy policy

"This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it".

## Security requirements

- MFA is required for the GitHub account used to maintain this repository.
- MFA is required for the SignPath account used to approve signing requests.
- Every signing request is manually approved by the approver role listed above.

## Release page requirement

All GitHub releases must include a visible `Code signing policy` link to this document.
