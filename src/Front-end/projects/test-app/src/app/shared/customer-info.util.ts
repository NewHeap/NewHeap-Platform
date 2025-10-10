export class CustomerViewModel {
  name: string = '';

  constructor(init?: Partial<CustomerViewModel>) {
    Object.assign(this, init);
  }
}

export class CustomerMapEntry {
  category: string = '';
  customers: CustomerViewModel[] = [];

  constructor(init?: Partial<CustomerMapEntry>) {
    Object.assign(this, init);
  }
}

export class CustomerInfo {
  entries: CustomerMapEntry[] = [];

  constructor(init?: Partial<CustomerInfo>) {
    Object.assign(this, init);
  }
}

export function globalGetCategories() {
  return ['A', 'B', 'C', 'D'];
}


export function globalCustomerInfo() {
  const categories = globalGetCategories();

  return new CustomerInfo({
    entries: [
      new CustomerMapEntry({
        category: 'A',
        customers: [
          new CustomerViewModel({name: 'Customer A1'}),
          new CustomerViewModel({name: 'Customer A2'})
        ]
      }),
      new CustomerMapEntry({
        category: 'B',
        customers: [
          new CustomerViewModel({name: 'Customer B1'}),
          new CustomerViewModel({name: 'Customer B2'})
        ]
      })
    ]
  });
}
