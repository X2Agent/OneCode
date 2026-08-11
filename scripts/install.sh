#!/usr/bin/env bash
# OneCode Installer for Linux/macOS
# Usage: curl -fsSL https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.sh | bash
set -euo pipefail

# ── 配置 ──────────────────────────────────────────────────────────
REPO_OWNER="${ONECODE_REPO_OWNER:-X2Agent}"
REPO_NAME="${ONECODE_REPO_NAME:-OneCode}"
INSTALL_DIR="${ONECODE_INSTALL_DIR:-$HOME/.onecode/bin}"
BINARY_NAME="onecode"
GITHUB_BASE="https://github.com/${REPO_OWNER}/${REPO_NAME}"

# ── 颜色 ──────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}▸${NC} $*"; }
ok()    { echo -e "${GREEN}✓${NC} $*"; }
warn()  { echo -e "${YELLOW}!${NC} $*"; }
err()   { echo -e "${RED}✗${NC} $*" >&2; }

# ── 检测平台 ──────────────────────────────────────────────────────
detect_platform() {
    local os arch
    case "$(uname -s)" in
        Linux*)  os="linux" ;;
        Darwin*) os="osx" ;;
        *)       err "不支持的操作系统: $(uname -s)"; exit 1 ;;
    esac

    case "$(uname -m)" in
        x86_64|amd64)  arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *)             err "不支持的架构: $(uname -m)"; exit 1 ;;
    esac

    echo "${os}-${arch}"
}

# ── 获取最新版本 ──────────────────────────────────────────────────
get_latest_version() {
    local api_url="https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/releases/latest"
    local response version

    if command -v curl &>/dev/null; then
        response=$(curl -fsSL \
            -H "Accept: application/vnd.github+json" \
            -H "User-Agent: onecode-installer" \
            -H "X-GitHub-Api-Version: 2022-11-28" \
            "$api_url")
    elif command -v wget &>/dev/null; then
        response=$(wget -qO- \
            --header="Accept: application/vnd.github+json" \
            --header="User-Agent: onecode-installer" \
            --header="X-GitHub-Api-Version: 2022-11-28" \
            "$api_url")
    else
        err "需要 curl 或 wget"; exit 1
    fi

    version=$(printf '%s' "$response" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)
    if [ -z "$version" ]; then
        err "GitHub latest release 响应不包含 tag_name: ${REPO_OWNER}/${REPO_NAME}"
        exit 1
    fi

    echo "$version"
}

# ── 下载并安装 ────────────────────────────────────────────────────
install_binary() {
    local platform="$1"
    local version="$2"
    local download_url
    local temp_dir
    local asset_version="${version#v}"

    # 约定: release asset 命名格式为 onecode-{semver}-{platform}.tar.gz，tag 保留 v 前缀。
    download_url="${GITHUB_BASE}/releases/download/${version}/onecode-${asset_version}-${platform}.tar.gz"

    temp_dir=$(mktemp -d)
    trap 'rm -rf "$temp_dir"' EXIT

    info "下载 OneCode ${version} (${platform})..."
    info "URL: ${download_url}"

    if command -v curl &>/dev/null; then
        curl -fSL --progress-bar -o "${temp_dir}/onecode.tar.gz" "$download_url"
        curl -fsSL -o "${temp_dir}/onecode.tar.gz.sha256" "${download_url}.sha256"
    elif command -v wget &>/dev/null; then
        wget -q --show-progress -O "${temp_dir}/onecode.tar.gz" "$download_url"
        wget -q -O "${temp_dir}/onecode.tar.gz.sha256" "${download_url}.sha256"
    else
        err "需要 curl 或 wget"; exit 1
    fi

    info "校验 SHA256..."
    local expected_hash actual_hash
    expected_hash=$(awk '{print $1}' "${temp_dir}/onecode.tar.gz.sha256")
    if command -v sha256sum &>/dev/null; then
        actual_hash=$(sha256sum "${temp_dir}/onecode.tar.gz" | awk '{print $1}')
    elif command -v shasum &>/dev/null; then
        actual_hash=$(shasum -a 256 "${temp_dir}/onecode.tar.gz" | awk '{print $1}')
    else
        err "需要 sha256sum 或 shasum 进行完整性校验"; exit 1
    fi
    if [ "${expected_hash}" != "${actual_hash}" ]; then
        err "SHA256 校验失败。期望 ${expected_hash}，实际 ${actual_hash}"; exit 1
    fi

    # 解压
    info "解压中..."
    local extract_dir="${temp_dir}/payload"
    mkdir -p "$extract_dir"
    tar -xzf "${temp_dir}/onecode.tar.gz" -C "$extract_dir"

    # 发布包包含 prompts、Team YAML 与 Playwright 资源，必须整体安装。
    if [ ! -f "${extract_dir}/OneCode.Cli" ]; then
        err "在归档根目录中找不到 OneCode.Cli"; exit 1
    fi

    mkdir -p "$INSTALL_DIR"
    cp -R "${extract_dir}/." "$INSTALL_DIR/"
    mv -f "${INSTALL_DIR}/OneCode.Cli" "${INSTALL_DIR}/${BINARY_NAME}"
    chmod +x "${INSTALL_DIR}/${BINARY_NAME}"

    ok "已安装到 ${INSTALL_DIR}/${BINARY_NAME}（含运行时资源）"
}

# ── 配置 PATH ─────────────────────────────────────────────────────
setup_path() {
    local shell_rc=""
    local path_line="export PATH=\"${INSTALL_DIR}:\$PATH\""

    case "${SHELL:-/bin/bash}" in
        */zsh)  shell_rc="$HOME/.zshrc" ;;
        */bash) shell_rc="$HOME/.bashrc" ;;
        */fish) shell_rc="$HOME/.config/fish/config.fish" ;;
    esac

    if [ -n "$shell_rc" ] && [ -f "$shell_rc" ]; then
        if ! grep -qF "$INSTALL_DIR" "$shell_rc" 2>/dev/null; then
            echo "" >> "$shell_rc"
            echo "# OneCode" >> "$shell_rc"
            echo "$path_line" >> "$shell_rc"
            ok "已将 ${INSTALL_DIR} 添加到 PATH (${shell_rc})"
            info "请运行: source ${shell_rc}"
        else
            info "PATH 已包含 ${INSTALL_DIR}"
        fi
    else
        warn "请手动将以下内容添加到你的 shell 配置:"
        echo "  ${path_line}"
    fi
}

# ── 创建 shell 别名/补全 ─────────────────────────────────────────
create_shell_alias() {
    local shell_rc=""
    case "${SHELL:-/bin/bash}" in
        */zsh)  shell_rc="$HOME/.zshrc" ;;
        */bash) shell_rc="$HOME/.bashrc" ;;
    esac

    if [ -n "$shell_rc" ] && [ -f "$shell_rc" ]; then
        if ! grep -qF "alias cc=" "$shell_rc" 2>/dev/null; then
            echo "" >> "$shell_rc"
            echo "# OneCode shortcut" >> "$shell_rc"
            echo "alias cc='${INSTALL_DIR}/${BINARY_NAME}'" >> "$shell_rc"
            ok "已创建快捷别名: cc → onecode"
        fi
    fi
}

# ── 主流程 ────────────────────────────────────────────────────────
main() {
    echo ""
    echo -e "${CYAN}╔══════════════════════════════════════════════════╗${NC}"
    echo -e "${CYAN}║       OneCode Installer                            ║${NC}"
    echo -e "${CYAN}╚══════════════════════════════════════════════════╝${NC}"
    echo ""

    local platform
    platform=$(detect_platform)
    info "检测平台: ${platform}"

    local version="${ONECODE_VERSION:-}"
    if [ -z "$version" ]; then
        version=$(get_latest_version)
        info "最新版本: ${version}"
    else
        info "指定版本: ${version}"
    fi

    install_binary "$platform" "$version"
    setup_path
    create_shell_alias

    echo ""
    echo -e "${GREEN}╔══════════════════════════════════════════════════╗${NC}"
    echo -e "${GREEN}║  ✅ 安装完成!                                     ║${NC}"
    echo -e "${GREEN}╚══════════════════════════════════════════════════╝${NC}"
    echo ""
    echo -e "  运行 ${CYAN}onecode --help${NC} 开始使用"
    echo -e "  或重新打开终端使 PATH 生效"
    echo ""
}

main "$@"
