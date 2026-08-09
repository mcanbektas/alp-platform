export interface AccountUser {
  displayName: string;
  email: string;
}

export interface AccountMenuProps {
  user: AccountUser | null;
  loginHref: string;
  registerHref: string;
  accountHref: string;
  onLogout: () => void;
}

/**
 * Kimlik durumu (user) her ürünün kendi AuthProvider'ından props ile akar —
 * bu paket tüm ürünler için ortak /api/auth'a değil, yalnız görünüme sahip.
 */
export function AccountMenu({ user, loginHref, registerHref, accountHref, onLogout }: AccountMenuProps) {
  if (!user) {
    return (
      <div className="alp-account-menu alp-account-menu--anon">
        <a href={loginHref} className="alp-account-menu__login">Giriş yap</a>
        <a href={registerHref} className="alp-account-menu__register">Kayıt ol</a>
      </div>
    );
  }

  return (
    <div className="alp-account-menu">
      <a href={accountHref} className="alp-account-menu__name">{user.displayName}</a>
      <button type="button" className="alp-account-menu__logout" onClick={onLogout}>
        Çıkış yap
      </button>
    </div>
  );
}
