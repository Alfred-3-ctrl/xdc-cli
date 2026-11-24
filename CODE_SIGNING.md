# Code signing policy

ZJU Learning Desktop is an independently maintained, unofficial tool for Zhejiang University students.

The existing Windows package is unsigned. The project is preparing its [SignPath Foundation](https://signpath.org/) application. Once production signing is approved and configured, the proposed attribution is “Free code signing provided by [SignPath.io](https://signpath.io/), certificate by SignPath Foundation”.

## Maintainer and approvals

[Alfred-3-ctrl](https://github.com/Alfred-3-ctrl) is the project's maintainer, author, reviewer, and release-signing approver. The maintainer reviews contributions from outside the project and manually approves each production request in SignPath after checking its source commit and artifact. GitHub and SignPath accounts with access to the signing process must use multi-factor authentication.

## What is signed

The [Windows release workflow](.github/workflows/release.yml) builds from source on GitHub-hosted Windows runners and runs the repository tests. For a production signing request, the workflow submits the repository's own native Windows host, `Xdc.NativeDesktop.exe`, as a GitHub Actions artifact. Its PE product name is `Xdc.NativeDesktop`. SignPath verifies the workflow origin, and the artifact configuration checks the product name and version reported by that build. The application ZIP also includes the pinned upstream Electron distribution for the local background service. The Foundation signing scope covers the project-built host.

The workflow verifies the returned host's Authenticode signature and timestamp, inserts that exact host into the ZIP, and generates a new checksum. The resulting `release.json` records the source commit, host SHA-256, and signature status. Maintainers verify the signed package and test the delivered EXE on Windows with Smart App Control enabled before identifying it as a signed release. For reproducibility instructions, see [the signing workflow guide](docs/signpath-release.md).

[Download releases](https://github.com/Alfred-3-ctrl/xdc-cli/releases) · [Privacy policy](PRIVACY.md)
