import type { ReactNode } from 'react';
import { AccountMenu, type AccountMenuProps } from './AccountMenu';
import { ProductSwitcher, type ProductSwitcherProps } from './ProductSwitcher';

export interface HeaderProps {
  products: ProductSwitcherProps['products'];
  currentProductId: string;
  account: AccountMenuProps;
  /** Ürüne özgü logo/başlık alanı — her SPA kendi markasını basar. */
  brand?: ReactNode;
}

export function Header({ products, currentProductId, account, brand }: HeaderProps) {
  return (
    <header className="alp-header">
      <div className="alp-header__brand">{brand}</div>
      <ProductSwitcher products={products} currentId={currentProductId} />
      <AccountMenu {...account} />
    </header>
  );
}
